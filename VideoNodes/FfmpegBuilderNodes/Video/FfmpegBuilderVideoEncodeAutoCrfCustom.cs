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
    /// Gets or sets if CPU should be used for the encoding
    /// </summary>
    [Boolean(2)]
    public bool UseCpu { get; set; }

    /// <summary>
    /// Gets or sets the mode to use for determing the VMAF
    /// </summary>
    [Select(nameof(VmafOptions), 3)]
    [DefaultValue(VmafMode.Default)] 
    public VmafMode Mode { get; set; } = VmafMode.Default;

    /// <summary>
    /// Gets or sets the minimum VMAF score
    /// </summary>
    [NumberFloat(4)]
    [DefaultValue(93f)]
    [ConditionEquals(nameof(Mode), VmafMode.Custom)]
    public float MinVmaf { get; set; } = 93f;

    /// <summary>
    /// The maximum bitrate allowed for encoding, in megabits per second (Mbps).
    /// Defaults to 11.5.
    /// </summary>
    [NumberFloat(5)]
    [DefaultValue(11.5f)]
    [ConditionEquals(nameof(Mode), VmafMode.Custom)]
    public float MaxBitrate { get; set; } = 11.5f;

    /// <summary>
    /// Gets or sets the number of samples to take 
    /// </summary>
    [NumberInt(6)]
    [DefaultValue(3)]
    [Range(1, 10)]
    [ConditionEquals(nameof(Mode), VmafMode.Custom)]
    public int Samples { get; set; } = 3;

    /// <summary>
    /// Gets or sets the length of a sample to take in seconds 
    /// </summary>
    [NumberInt(7)]
    [DefaultValue(20)]
    [Range(1, 60)]
    [ConditionEquals(nameof(Mode), VmafMode.Custom)]
    public int SampleLengthSeconds { get; set; } = 20;

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
    
    /// <summary>
    /// Gets a list of available VMAF options for encoding
    /// </summary>
    private static List<ListOption> VmafOptions = new ()
    {
        new () { Label = $"Flow.Parts.{nameof(FfmpegBuilderVideoEncodeAutoCrfCustom)}.Enums.{nameof(VmafMode)}.{nameof(VmafMode.Default)}", Value = VmafMode.Default },
        new () { Label = $"Flow.Parts.{nameof(FfmpegBuilderVideoEncodeAutoCrfCustom)}.Enums.{nameof(VmafMode)}.{nameof(VmafMode.Deep)}", Value = VmafMode.Deep },
        new () { Label = $"Flow.Parts.{nameof(FfmpegBuilderVideoEncodeAutoCrfCustom)}.Enums.{nameof(VmafMode)}.{nameof(VmafMode.Custom)}", Value = VmafMode.Custom },
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

        float maxBitrate = Mode is VmafMode.Custom ? MaxBitrate : 11.5f;
        var targetBitRate = maxBitrate * 1024 * 1024;
        float minVmaf = Mode is VmafMode.Custom ? MinVmaf : 94f;
        int sampleLengthSeconds = Mode switch
        {
            VmafMode.Default => 10,
            VmafMode.Deep => 20,
            _ => SampleLengthSeconds > 2 ? SampleLengthSeconds : 10
        };
        int samples = Mode switch
        {
            VmafMode.Default => 3,
            VmafMode.Deep => 5,
            _ => Samples > 1 ? SampleLengthSeconds : 3
        };

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
            args.Logger?.WLog($"Bitrate is {GeneralHelper.HumanizeBitrate(videoBitRate)}, higher than {MaxBitrate} Mbps");
            args.Logger?.ILog("Will fallback to bitrate encoding");
            forceEncode = true;
        }

        // The bitrate is good so we check if the codec is already hevc
        if (forceEncode == false && Codec.Equals(currentCodec, StringComparison.CurrentCultureIgnoreCase))
        {
            args.Logger?.ILog($"Bitrate ({videoBitRate}) and codec ({currentCodec}) acceptable, skipping encode.");
            return 2;
        }

        string encoder = GetEncoder(args);

        var optimizer = new VmafCrfOptimizer(args, FFMPEG, localFile, video.Stream.FramesPerSecond, video.Stream.Duration);

        var optimized = optimizer.Optimize(video, encoder, preset,
            minVmaf: minVmaf,
            numberOfChunks: samples,
            chunkSeconds: sampleLengthSeconds,
            forceEncoding: forceEncode);

        return optimized ? 1 : 2;
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
        bool noVaapi =
            args.Variables.Any(x => x.Key?.ToLowerInvariant() == "novaapi" && x.Value as bool? == true);
        bool noAmf =
            args.Variables.Any(x =>
                (x.Key?.ToLowerInvariant() == "noamf" || x.Key?.ToLowerInvariant() == "noamd") &&
                x.Value as bool? == true);
        bool noVideoToolbox =
            args.Variables.Any(x => x.Key?.ToLowerInvariant() == "novideotoolbox" && x.Value as bool? == true);
        bool noVulkan =
            args.Variables.Any(x => x.Key?.ToLowerInvariant() == "novulkan" && x.Value as bool? == true);
        bool noDxva2 = OperatingSystem.IsWindows() == false || 
                       args.Variables.Any(x => x.Key?.ToLowerInvariant() == "nodxva2" && x.Value as bool? == true);
        bool noD3d11va = OperatingSystem.IsWindows() == false || 
                         args.Variables.Any(x => x.Key?.ToLowerInvariant() == "nod3d11va" && x.Value as bool? == true);
        bool noOpencl =
            args.Variables.Any(x => x.Key?.ToLowerInvariant() == "noopencl" && x.Value as bool? == true);

        switch (Codec)
        {
            case "hevc":
            {
                if (noQsv == false && CanUseHardwareEncoding.CanProcess_Qsv_Hevc(args))
                    return "hevc_qsv";
                if (noNvidia == false && CanUseHardwareEncoding.CanProcess_Nvidia_Hevc(args))
                    return "hevc_nvenc";
                if (noAmf == false && CanUseHardwareEncoding.CanProcess_Amd_Hevc(args))
                    return "hevc_amf";
                if (noVaapi == false && CanUseHardwareEncoding.CanProcess_Vulkan_Hevc(args))
                    return "hevc_vulkan";
                if (noVaapi == false && CanUseHardwareEncoding.CanProcess_Vaapi_Hevc(args))
                    return "hevc_vaapi";
                
                return "libx265";
            }
            case "h264":
            {
                if (noQsv == false && CanUseHardwareEncoding.CanProcess_Qsv_H264(args))
                    return "h264_qsv";
                if (noNvidia == false && CanUseHardwareEncoding.CanProcess_Nvidia_H264(args))
                    return "h264_nvenc";
                if (noAmf == false && CanUseHardwareEncoding.CanProcess_Amd_H264(args))
                    return "h264_amf";
                if (noVaapi == false && CanUseHardwareEncoding.CanProcess_Vaapi_H264(args))
                    return "h264_vaapi";
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

    public enum VmafMode
    {
        Default = 0,
        Deep = 1, // dont like this name
        Custom = 2
    }
}