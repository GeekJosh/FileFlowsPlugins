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
    /// Gets or sets the low value for the VMAF testing
    /// </summary>
    [NumberFloat(8)] 
    [DefaultValue(15)] 
    [ConditionEquals(nameof(Mode), VmafMode.Custom)]
    public float CrfLow { get; set; } = 15f;
    
    /// <summary>
    /// Gets or sets the high value for the VMAF testing
    /// </summary>
    [NumberFloat(8)] 
    [DefaultValue(25)] 
    [ConditionEquals(nameof(Mode), VmafMode.Custom)]
    public float CrfHigh { get; set; } = 25f;

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
        new () { Label = $"Flow.Parts.{nameof(FfmpegBuilderVideoEncodeVmaf)}.Enums.{nameof(VmafMode)}.{nameof(VmafMode.Default)}", Value = VmafMode.Default },
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

        float maxBitrate = Mode is VmafMode.Custom ? MaxBitrate : 11.5f;
        var targetBitRate = maxBitrate * 1024 * 1024;
        float minVmaf = Mode is VmafMode.Custom ? MinVmaf : 94f;
        int sampleLengthSeconds = Mode switch
        {
            VmafMode.Default => 10,
            VmafMode.Thorough => 20,
            _ => SampleLengthSeconds > 2 ? SampleLengthSeconds : 10
        };
        int samples = Mode switch
        {
            VmafMode.Default => 3,
            VmafMode.Thorough => 5,
            _ => Samples > 1 ? SampleLengthSeconds : 3
        };
        float crfLow = Mode switch
        {
            VmafMode.Default => 15,
            VmafMode.Thorough => 15,
            _ => CrfLow > 3 ? CrfLow : 15
        };
        float crfHigh = Mode switch
        {
            VmafMode.Default => 25,
            VmafMode.Thorough => 25,
            _ => CrfHigh > crfLow ? CrfHigh : Math.Max(25, crfLow + 5)
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
            args.Logger?.WLog($"Bitrate is {GeneralHelper.HumanizeBitrate(videoBitRate)}, higher than {MaxBitrate} Mbps");
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

        var optimizer = new VmafCrfOptimizer(args, FFMPEG, localFile, video.Stream.FramesPerSecond, video.Stream.Duration);

        var optimized = optimizer.Optimize(video, encoder, preset,
            minVmaf: minVmaf,
            crfStart: crfLow,
            crfEnd:  crfHigh,
            numberOfChunks: samples,
            chunkSeconds: sampleLengthSeconds,
            forceEncoding: forceEncode);

        return optimized ? 1 : 2;
    }

    public enum VmafMode
    {
        Default = 0,
        Thorough = 1,
        Custom = 2
    }
}