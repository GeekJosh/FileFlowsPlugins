using System.Globalization;
using System.IO;
using FileFlows.VideoNodes.FfmpegBuilderNodes.Models;
using FileFlows.VideoNodes.Helpers;

namespace FileFlows.VideoNodes.FfmpegBuilderNodes;

/// <summary>
/// VMAF baed encoding
/// </summary>
public class FfmpegBuilderVideoEncodeVmaf : FfmpegBuilderNode
{
    /// <inheritdoc />
    public override int Inputs => 1;

    /// <inheritdoc />
    public override int Outputs => 2;

    /// <inheritdoc />
    public override string HelpUrl =>
        "https://fileflows.com/docs/plugins/video-nodes/ffmpeg-builder/video-encode-vmaf";

    /// <summary>
    /// The codec to use for encoding. Options include "h264", "hevc", "av1", etc.
    /// Defaults to "hevc".
    /// </summary>
    [Select(nameof(CodecOptions), 1)]
    [DefaultValue("hevc")]
    public string Codec { get; set; }

    /// <summary>
    /// Gets or sets the encoder to use
    /// </summary>
    [Select(nameof(EncoderOptions), 3)]
    [DefaultValue("")]
    public string Encoder { get; set; } = string.Empty;

    /// <summary>
    /// Gets the encoders options
    /// </summary>
    public static List<ListOption> EncoderOptions => VideoHelper.Encoders;
    
    /// <summary>
    /// Gets or sets the mode to use for determing the VMAF
    /// </summary>
    [Select(nameof(VmafOptions), 3)]
    [DefaultValue(VmafMode.Balanced)] 
    public VmafMode Mode { get; set; } = VmafMode.Balanced;

    /// <summary>
    /// Gets or sets te maximum size a file is estimated to be to do the encodce
    /// </summary>
    [Slider(4)]
    [Range(1, 100)]
    [DefaultValue(90)]
    public float MaxSizePercent { get; set; } = 90f;

    /// <summary>
    /// Gets or sets the minimum VMAF score
    /// </summary>
    [NumberFloat(5)]
    [DefaultValue(93f)]
    [ConditionEquals(nameof(Mode), VmafMode.Custom)]
    public float MinVmaf { get; set; } = 93f;

    /// <summary>
    /// The maximum bitrate allowed for encoding, in kilobits per second (Kbps).
    /// </summary>
    [NumberInt(6)]
    [DefaultValue(10_000)]
    [ConditionEquals(nameof(Mode), VmafMode.Custom)]
    public float MaxBitrate { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets the number of samples to take 
    /// </summary>
    [NumberInt(7)]
    [DefaultValue(3)]
    [Range(1, 10)]
    [ConditionEquals(nameof(Mode), VmafMode.Custom)]
    public int Samples { get; set; } = 3;

    /// <summary>
    /// Gets or sets the length of a sample to take in seconds 
    /// </summary>
    [NumberInt(8)]
    [DefaultValue(20)]
    [Range(1, 60)]
    [ConditionEquals(nameof(Mode), VmafMode.Custom)]
    public int SampleLengthSeconds { get; set; } = 20;

    /// <summary>
    /// Gets or sets the low value for the VMAF testing
    /// </summary>
    [NumberFloat(20)] 
    [DefaultValue(15)] 
    [ConditionEquals(nameof(Mode), VmafMode.Custom)]
    public float CrfLow { get; set; } = 15f;
    
    /// <summary>
    /// Gets or sets the high value for the VMAF testing
    /// </summary>
    [NumberFloat(21)] 
    [DefaultValue(25)] 
    [ConditionEquals(nameof(Mode), VmafMode.Custom)]
    public float CrfHigh { get; set; } = 25f;

    /// <summary>
    /// Gets or sets the FPS to run the VMAF in
    /// </summary>
    [NumberInt(30)]
    [ConditionEquals(nameof(Mode), VmafMode.Custom)]
    public int VmafFps { get; set; } = 0;

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
    public static List<ListOption> VmafOptions => new ()
    {
        new () { Label = $"Flow.Parts.{nameof(FfmpegBuilderVideoEncodeVmaf)}.Enums.{nameof(VmafMode)}.{nameof(VmafMode.Balanced)}", Value = VmafMode.Balanced },
        new () { Label = $"Flow.Parts.{nameof(FfmpegBuilderVideoEncodeVmaf)}.Enums.{nameof(VmafMode)}.{nameof(VmafMode.FastScan)}", Value = VmafMode.FastScan },
        new () { Label = $"Flow.Parts.{nameof(FfmpegBuilderVideoEncodeVmaf)}.Enums.{nameof(VmafMode)}.{nameof(VmafMode.Thorough)}", Value = VmafMode.Thorough },
        new () { Label = $"Flow.Parts.{nameof(FfmpegBuilderVideoEncodeVmaf)}.Enums.{nameof(VmafMode)}.{nameof(VmafMode.Custom)}", Value = VmafMode.Custom },
    };

    /// <inheritdoc />
    public override int Execute(NodeParameters args)
    {
        if (Model.VideoInfo.VideoStreams?.Any() != true)
            return args.Fail("No video streams found.");

        var video = Model.VideoStreams?.FirstOrDefault(x => x.Deleted == false);
        if (video?.Stream == null)
            return args.Fail("No video stream");
        
        string error = string.Empty;

        string codec = Codec?.EmptyAsNull() ?? "hevc";
        

        string currentCodec = video.Stream.Codec?.ToLowerInvariant() ?? string.Empty;

        var localFileResult = args.FileService.GetLocalPath(args.WorkingFile);
        if (localFileResult.Failed(out error))
            return args.Fail(error);

        var localFile = localFileResult.Value;

        var videoBitRate = VideoHelper.GetBitrate(args, Model.VideoInfo, localFile);
        if (videoBitRate <= 0)
            return args.Fail("Unable to determine video bitrate");

        float maxBitrate = Mode is VmafMode.Custom && MaxBitrate > 100 ? MaxBitrate : 10_000;
        float maxPercent = MaxSizePercent < 1 ? 90 : Math.Clamp(MaxSizePercent, 1, 100);
        var targetBitRate = maxBitrate * 1000;
        float minVmaf = Mode is VmafMode.Custom ? MinVmaf : 95.5f;
        int samples = Mode switch
        {
            VmafMode.FastScan => 2,
            VmafMode.Balanced => 3,
            VmafMode.Thorough => 5,
            VmafMode.Custom => Samples > 1 ? Samples : 3,
            _ => 3
        };

        int sampleLengthSeconds = Mode switch
        {
            VmafMode.FastScan => 8,
            VmafMode.Balanced => 12,
            VmafMode.Thorough => 20,
            VmafMode.Custom => SampleLengthSeconds > 2 ? SampleLengthSeconds : 10,
            _ => 12
        };

        int vmafFps = Mode switch
        {
            VmafMode.FastScan => 15,
            VmafMode.Balanced => 15,
            VmafMode.Thorough => 0, // use full native fps for best accuracy
            VmafMode.Custom => VmafFps,
            _ => 15
        };

        float crfLow = Mode switch
        {
            VmafMode.FastScan => 20f,
            VmafMode.Balanced => 16f,
            VmafMode.Thorough => 15f,
            VmafMode.Custom => CrfLow > 3 ? CrfLow : 15f,
            _ => 15f
        };

        float crfHigh = Mode switch
        {
            VmafMode.FastScan => 24f,
            VmafMode.Balanced => 24f,
            VmafMode.Thorough => 24f,
            VmafMode.Custom => CrfHigh > crfLow ? CrfHigh : Math.Max(24, crfLow + 5),
            _ => 24f
        };

        // Video Description
        var videoDescription = $"{GeneralHelper.HumanizeBitrate(videoBitRate)} {codec}";
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
            args.Logger?.WLog($"Bitrate is {GeneralHelper.HumanizeBitrate(videoBitRate)}, higher than {GeneralHelper.HumanizeBitrate(maxBitrate)}");
            args.Logger?.ILog("Will fallback to bitrate encoding");
            forceEncode = true;
        }

        // The bitrate is good so we check if the codec is already hevc
        if (forceEncode == false && codec.Equals(currentCodec, StringComparison.CurrentCultureIgnoreCase))
        {
            args.Logger?.ILog($"Bitrate ({videoBitRate}) and codec ({currentCodec}) acceptable, skipping encode.");
            return 2;
        }

        string encoder = VideoHelper.GetEncoder(args, Encoder, codec);

        var ffmpeg = GetFFmpegExecutable(args);

        var optimizer = new VmafCrfOptimizer(args, FFMPEG, ffmpeg, localFile, video.Stream.FramesPerSecond, video.Stream.Duration);
        optimizer.CrfTesting += (crf, percent) =>
        {
            args.RecordAdditionalInfo("VMAF Step", $"Testing {crf:F1} CRF", 1, null);
            args.PartPercentageUpdate?.Invoke(percent);
        };
        optimizer.VmafStep += (text) =>
        {
            args.RecordAdditionalInfo("Status", text?.EmptyAsNull(), 1, null);
        };

        args.RecordAdditionalInfo("VMAF Step", "Extracting Samples", 1, null);
        
        var optimized = optimizer.Optimize(video, encoder, preset,
            minVmaf: minVmaf,
            crfStart: crfLow,
            crfEnd:  crfHigh,
            numberOfChunks: samples,
            chunkSeconds: sampleLengthSeconds,
            forceEncoding: forceEncode,
            maxSizePercent: maxPercent,
            vmafFps: vmafFps
        );

        return optimized ? 1 : 2;
    }

    /// <summary>
    /// Gets the FFmpeg version to use for VMAF
    /// </summary>
    /// <param name="args">the node parameters</param>
    /// <returns>the FFmpeg version to use for VMAF</returns>
    private string GetFFmpegExecutable(NodeParameters args)
    {
        var ffmpeg = args.GetToolPath("FFmpegVMAF");
        if(string.IsNullOrWhiteSpace(ffmpeg) == false)
            return ffmpeg;

        if (args.IsDocker == false) 
            return FFMPEG;
        
        if (File.Exists("/app/common/ffmpeg-static/ffmpeg"))
            return "/app/common/ffmpeg-static/ffmpeg";
        if(File.Exists("/opt/ffmpeg-static/bin/ffmpeg"))
            return "/opt/ffmpeg-static/bin/ffmpeg";

        return FFMPEG;
    }

    /// <summary>
    /// Different VMAF modes
    /// </summary>
    public enum VmafMode
    {
        /// <summary>
        /// Moderate sampling and length, good balance between accuracy and runtime.
        /// </summary>
        Balanced = 0,
        /// <summary>
        /// Small number of samples, shorter sample length, moderate FPS, suitable for quick approximations.
        /// </summary>
        FastScan = 1,
        /// <summary>
        ///  More samples, longer chunks, full native FPS, highest accuracy but slowest.
        /// </summary>
        Thorough = 2,
        /// <summary>
        /// Custom scan
        /// </summary>
        Custom = 3
    }
}