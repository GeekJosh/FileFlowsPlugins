using System.Globalization;
using System.IO;
using FileFlows.VideoNodes.FfmpegBuilderNodes.Models;
using FileFlows.VideoNodes.Helpers;

namespace FileFlows.VideoNodes.FfmpegBuilderNodes;

/// <summary>
/// AutoCRF encoder using ab-av1's crf-search
/// </summary>
public class FfmpegBuilderVideoEncodeAutoCrfCustom : FfmpegBuilderNode
{
    /// <inheritdoc />
    public override int Inputs => 1;

    /// <inheritdoc />
    public override int Outputs => 2;

    /// <inheritdoc />
    public override string HelpUrl =>
        "https://fileflows.com/docs/plugins/video-nodes/ffmpeg-builder/video-encode-auto-crf";

    /// <summary>
    /// The codec to use for encoding. Options include "h264", "hevc", "av1", etc.
    /// Defaults to "hevc".
    /// </summary>
    [Select(nameof(CodecOptions), 1)]
    [DefaultValue("hevc")]
    public string Codec { get; set; }

    /// <summary>
    /// The maximum bitrate allowed for encoding, in megabits per second (Mbps).
    /// Defaults to 11.5.
    /// </summary>
    [NumberFloat(2)]
    [DefaultValue(11.5f)]
    public float MaxBitrate { get; set; } = 11.5f;
    
    /// <summary>
    /// Gets or sets if CPU should be used for the encoding
    /// </summary>
    [Boolean(5)]
    public bool UseCpu { get; set; }

    /// <summary>
    /// Gets the list of available codec options for encoding.
    /// Each option has a label and a corresponding codec value.
    /// </summary>
    public static List<ListOption> CodecOptions => new()
    {
        new() { Label = "H.264", Value = "h264" },
        new() { Label = "HEVC", Value = "hevc" },
        new() { Label = "AV1", Value = "av1" }
    };

    private string ffmpegBtbn;

    /// <inheritdoc />
    public override int Execute(NodeParameters args)
    {
        if (Model.VideoInfo.VideoStreams?.Any() != true)
            return args.Fail("No video streams found.");

        var video = Model.VideoStreams?.FirstOrDefault(x => x.Deleted == false);
        if (video?.Stream == null)
            return args.Fail("No video stream");
        
        string error = string.Empty;

        Codec = Codec?.EmptyAsNull() ?? "hevc";

        string currentCodec = video.Stream.Codec?.ToLowerInvariant() ?? string.Empty;

        var localFileResult = args.FileService.GetLocalPath(args.WorkingFile);
        if (localFileResult.Failed(out error))
            return args.Fail(error);

        var localFile = localFileResult.Value;

        var videoBitRate = VideoHelper.GetBitrate(args, Model.VideoInfo, localFile);
        if (videoBitRate <= 0)
            return args.Fail("Unable to determine video bitrate");

        var targetBitRate = MaxBitrate * 1024 * 1024;

        // Video Description
        var videoDescription = $"{GeneralHelper.HumanizeBitrate(videoBitRate)} {Codec}";
        List<string> videoColors = [];
        if (video.Stream.HDR)
            videoColors.Add("HDR");

        if (video.Stream.DolbyVision)
            videoColors.Add("DoVi");

        if (videoColors.Count > 0)
            videoDescription += " (" + string.Join(" / ", videoColors) + " )";

        // Okay, Let's go!
        var forceEncode = false;
        var preset = "slow";

        args.Logger?.ILog($"Video is {videoDescription}");

        // if we're cropping black bars, we will force the encode
        bool croppingBlackBars =
            video.Filter?.Any(x => x?.StartsWith("crop=", StringComparison.InvariantCultureIgnoreCase) == true) == true;
        
        forceEncode |= croppingBlackBars;

        // If the bitrate is more than we want then we don't care what the codec is
        if (videoBitRate > targetBitRate)
        {
            args.Logger?.WLog("Unacceptable bitrate");
            args.Logger?.WLog($"Bitrate is {GeneralHelper.HumanizeBitrate(videoBitRate)}, higher than {MaxBitrate} MBps");
            args.Logger?.ILog("Will fallback to bitrate encoding");
            forceEncode = true;
        }
        else
        {
            targetBitRate = videoBitRate;
        }

        // The bitrate is good so we check if the codec is already hevc
        if (forceEncode == false && Codec.Equals(currentCodec, StringComparison.CurrentCultureIgnoreCase))
        {
            args.Logger?.ILog($"Bitrate ({videoBitRate}) and codec ({currentCodec}) acceptable, skipping encode.");
            return 2;
        }

        string encoder = GetEncoder(args);
        List<string> command = [encoder, "-preset", preset];
        var pixelFormat = GetPixelFormat(video, encoder, command);

        var optimizer = new VmafCrfOptimizer(args, FFMPEG, localFile);
        
        var result = optimizer.FindBestCrf(encoder, pixelFormat, preset,
            crfStart:10, crfEnd: 14, numberOfChunks: 2, chunkSeconds:30);
        

        if (result.shouldReencode)
        {
            var crf_arg = GetCrfArg(encoder);
            var best = result.bestResult;
            video.EncodingParameters.Clear();
            video.EncodingParameters.AddRange(command);
            video.EncodingParameters.AddRange([$"{crf_arg}:v", result.bestCrf.ToString(CultureInfo.InvariantCulture)]);
            return 1;
        }
        
        if(result.shouldReencode == false && forceEncode == false)
        {
            args.Logger?.ILog("Falling back to copy as codec and bitrate are acceptable");
            return 2;
        }
        
        video.EncodingParameters.Clear();
        video.EncodingParameters.AddRange(command);
        
        var t = targetBitRate / 1024.00 / 1024.00;

        video.AdditionalParameters.AddRange([
            "-b:v:{index}", $"{t:F2}M",
            "-minrate", $"{(t * 0.75):F2}M",
            "-maxrate", $"{(t * 1.25):F2}M",
            "-bufsize", $"{Math.Round(t)}M"
        ]);
        args.Logger?.ILog(
            $"Falling back to bitrate encoding as video is unacceptable {GeneralHelper.HumanizeBitrate(targetBitRate)}");
        args.RecordAdditionalInfo("Score", "Not found", 1000, null);
        args.RecordAdditionalInfo("CRF", GeneralHelper.HumanizeBitrate(targetBitRate), 1000, null);

        // Falling back bitrate encode as we could not find a suitable CRF
        return 1;
    }

    private string GetPixelFormat(FfmpegVideoStream videoStream, string encoder, List<string> command)
    {
        var videoPixelFormat = encoder.Contains("qsv") ? "nv12" : "yuv420p";

        if (videoStream.Stream.Is10Bit)
        {
            videoPixelFormat = "yuv420p10le";
            if (encoder.Contains("hevc", StringComparison.InvariantCultureIgnoreCase))
                command.AddRange(["-pix_fmt:v:0", "p010le", "-profile:v:0", "main10"]);
        }

        return videoPixelFormat;
    }

    /// <summary>
    /// Gets the encoder to use
    /// </summary>
    /// <param name="args">the node parameters</param>
    /// <returns>the encoder to use</returns>
    private string GetEncoder(NodeParameters args)
    {
        bool noNvidia = UseCpu || 
            args.Variables.Any(x => x.Key?.ToLowerInvariant() == "nonvidia" && x.Value as bool? == true);
        bool noQsv = UseCpu ||
                     args.Variables.Any(x => x.Key?.ToLowerInvariant() == "noqsv" && x.Value as bool? == true);

        switch (Codec)
        {
            case "hevc":
            {
                if (noQsv == false && CanUseHardwareEncoding.CanProcess_Qsv_Hevc(args))
                    return "hevc_qsv";
                if (noNvidia == false && CanUseHardwareEncoding.CanProcess_Nvidia_Hevc(args))
                    return "hevc_nvenc";
                return "libx265";
            }
            case "h264":
            {
                if (noQsv == false && CanUseHardwareEncoding.CanProcess_Qsv_H264(args))
                    return "h264_qsv";
                if (noNvidia == false && CanUseHardwareEncoding.CanProcess_Nvidia_H264(args))
                    return "h264_nvenc";
                return "libx264";
            }
            case "av1":
            {
                if (noQsv == false && CanUseHardwareEncoding.CanProcess_Qsv_AV1(args))
                    return "av1_qsv";
                if (noNvidia == false && CanUseHardwareEncoding.CanProcess_Nvidia_AV1(args))
                    return "av1_nvenc";
                return "libsvtav1";
            }
        }

        return Codec.ToLower();
    }
    
    /// <summary>
    /// Gets the appropriate CRF argument name for a given codec.
    /// </summary>
    /// <param name="codec">The codec name (e.g., "h264_nvenc", "hevc_qsv").</param>
    /// <returns>
    /// The command-line argument to specify CRF or quality level for the codec.
    /// Examples: "-cq" for nvenc, "-q" for vaapi, "-global_quality" for qsv, or "-crf" as default.
    /// </returns>
    private static string GetCrfArg(string codec)
    {
        codec = codec.ToLowerInvariant();
        if (codec.Contains("nvenc")) return "-cq";
        if (codec.Contains("vaapi")) return "-q";
        if (codec.Contains("qsv")) return "-global_quality";
        return "-crf";
    }

}