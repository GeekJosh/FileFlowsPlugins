using System.Globalization;
using System.IO;
using FileFlows.VideoNodes.FfmpegBuilderNodes.Models;
using FileFlows.VideoNodes.Helpers;

namespace FileFlows.VideoNodes.FfmpegBuilderNodes;

/// <summary>
/// AutoCRF encoder using ab-av1's crf-search
/// </summary>
public class FfmpegBuilderVideoEncodeAutoCrf : FfmpegBuilderNode
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
    public float MaxBitrate { get; set; }

    /// <summary>
    /// Whether to apply a fix for Dolby Vision profile 5 decoding issues.
    /// Defaults to false.
    /// </summary>
    [Boolean(3)]
    [DefaultValue(false)]
    public bool FixDolby5 { get; set; }

    /// <summary>
    /// Whether to treat failure to find a suitable CRF as an error and stop processing.
    /// Defaults to false.
    /// </summary>
    [Boolean(4)]
    [DefaultValue(false)]
    public bool ErrorOnFail { get; set; }

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

    private string ffmpegBtbn, ffmpegJellyfin;

    /// <inheritdoc />
    public override int Execute(NodeParameters args)
    {
        string error = string.Empty;
        // Checking dependencies
        string abAv1 = args.GetToolPath("ab-av1")?.EmptyAsNull("ab-av1");
        if (string.IsNullOrWhiteSpace(abAv1))
        {
            abAv1 =  "/app/common/autocrf/ab-av1";
            if (File.Exists(abAv1) == false)
                abAv1 =  "/opt/autocrf/ab-av1";
            if (File.Exists(abAv1) == false)
                return args.Fail("Could not find ab-av1 file");
        }

        if (LoadFFmpegs(args) == -1)
            return -1;
        
        if (Model.VideoInfo.VideoStreams?.Any() != true)
            return args.Fail("No video streams found.");


        Codec = Codec?.EmptyAsNull() ?? "hevc";

        var video = Model.VideoStreams?.FirstOrDefault(x => x.Deleted == false);
        if (video?.Stream == null)
            return args.Fail("No video stream");

        string currentCodec = video.Stream.Codec?.ToLowerInvariant() ?? string.Empty;

        var localFileResult = args.FileService.GetLocalPath(args.WorkingFile);
        if (localFileResult.Failed(out error))
            return args.Fail(error);

        var localFile = localFileResult.Value;

        var videoBitRate = VideoHelper.GetBitrate(args, Model.VideoInfo, localFile);
        if (videoBitRate <= 0)
            return args.Fail("Unable to determine video bitrate");


        var targetBitRate = MaxBitrate * 1024 * 1024;
        var bitratePercent = (int)Math.Floor((100 / videoBitRate) * targetBitRate);

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
        var firstTryPercentage = 85;
        var secondTryPercentage = 100;
        var firstTryScore = 97;
        var secondTryScore = 95;
        var preset = "slow";

        args.Logger?.ILog($"Video is {videoDescription}");

        // if we're cropping black bars, we will force the encode
        bool croppingBlackBars =
            video.Filter?.Any(x => x?.StartsWith("crop=", StringComparison.InvariantCultureIgnoreCase) == true) == true;
        
        forceEncode |= DolbyVisionFix(args, video);
        forceEncode |= croppingBlackBars;

        // If the bitrate is more than we want then we don't care what the codec is
        if (videoBitRate > targetBitRate)
        {
            args.Logger?.WLog("Unacceptable bitrate");
            args.Logger?.WLog($"Bitrate is {GeneralHelper.HumanizeBitrate(videoBitRate)}, higher than {MaxBitrate} MBps");
            args.Logger?.ILog("Will fallback to bitrate encoding");
            forceEncode = true;

            if (firstTryPercentage > bitratePercent)
            {
                firstTryPercentage = bitratePercent;
            }

            secondTryPercentage = bitratePercent;
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

        args.Logger?.ILog($"Targeting {firstTryPercentage}% size, {firstTryScore}% VMAF");
        var attempt = CrfSearch(args, abAv1, localFile, encoder, preset,
            85, 97, videoBitRate, video);

        if (attempt.Winner == null)
        {
            args.Logger?.ILog(
                $"First attempt failed retrying with {secondTryPercentage}% size, {secondTryScore}% VMAF");
            attempt = CrfSearch(args, abAv1, localFile, encoder, preset,
                secondTryPercentage, secondTryScore, videoBitRate, video);
        }

        if (attempt.Winner != null)
        {
            var crf_arg = GetCrfArg(encoder);
            video.EncodingParameters.Clear();
            video.EncodingParameters.AddRange(attempt.Command);
            video.EncodingParameters.AddRange([$"{crf_arg}:v", attempt.Winner.Crf]);
            //args.Variables["ManualParameters"] = $"{attempt.Command} {crf_arg}:v {attempt.Winner.Crf}";
            args.Logger.ILog($"Attempt successful with {attempt.Winner.Size}% size, {attempt.Winner.Score}% VMAF");
            args.AdditionalInfoRecorder("Score", attempt.Winner.Score, 1000, null);
            args.AdditionalInfoRecorder("CRF", attempt.Winner.Crf, 1000, null);
            return 1;
        }

        if (attempt.Error)
        {
            args.Logger?.ELog(attempt.Message);
            return args.Fail($"AutoCRF: {attempt.Message}");
        }


        // fallback
        if (forceEncode == false)
        {
            args.Logger?.ILog("Falling back to copy as codec and bitrate are acceptable");
            return 2;
        }

        // setup bitrate encode
        //args.Variables["ManualParameters"] = string.Join(" ", attempt.Command);
        
        video.EncodingParameters.Clear();
        video.EncodingParameters.AddRange(attempt.Command);
        
        var t = targetBitRate / 1024.00 / 1024.00;

        video.AdditionalParameters.AddRange([
            "-b:v:{index}",
            $"{t:F2}M",
            "-minrate",
            $"{(t * 0.75):F2}M",
            "-maxrate",
            $"{(t * 1.25):F2}M",
            "-bufsize",
            $"{Math.Round(t)}M"
        ]);
        args.Logger?.ILog(
            $"Falling back to bitrate encoding as video is unacceptable {GeneralHelper.HumanizeBitrate(targetBitRate)}");
        args.AdditionalInfoRecorder("Score", "Not found", 1000, null);
        args.AdditionalInfoRecorder("CRF", GeneralHelper.HumanizeBitrate(targetBitRate), 1000, null);

        // Falling back bitrate encode as we could not find a suitable CRF
        return 1;
    }

    private int LoadFFmpegs(NodeParameters args)
    {
        var btbnResult = FindFFmpegVersion(args, "FFmpeg-Btbn", "/app/common/ffmpeg-static", "/opt/ffmpeg-static/bin");
        if (btbnResult.Failed(out var error))
            return args.Fail(error);
        ffmpegBtbn = btbnResult.Value;

        var jfResult = FindFFmpegVersion(args, "FFmpeg", "/usr/local/bin");
        if (jfResult.Failed(out error))
            return args.Fail(error);
        ffmpegJellyfin = jfResult.Value;

        return 1;
    }

    private Result<string> FindFFmpegVersion(NodeParameters args, string variable, params string[] paths)
    {
        var tool = args.GetToolPath(variable)?.EmptyAsNull(variable);
        if (string.IsNullOrWhiteSpace(tool) == false)
            return tool;
        
        foreach (var path in paths)
        {
            string fullPath = Path.Combine(path, "ffmpeg");
            if (File.Exists(fullPath))
                return fullPath;
        }

        return Result<string>.Fail($"FFmpeg  {variable} not found in any provided paths: " + string.Join(", ", paths));
        
    }

    private bool DolbyVisionFix(NodeParameters args, FfmpegVideoStream video)
    {
        bool forceEncode = false;
        if (video.Stream.DolbyVision == false || video.Stream.HDR  || FixDolby5 == false) 
            return forceEncode;
        
        args.Logger?.ILog("Video is DoVi without a fallback, so were creating one");
        forceEncode = true;
        args.Logger?.ILog("Testing for openCL");
        var processResult = args.Execute(new ExecuteArgs()
        {
            Command = ffmpegBtbn,
            ArgumentList =
            [
                "-hwaccel", "opencl", "-f", "lavfi", "-i", "testsrc=size=640x480:rate=25", "-t", "1", "-c:v",
                "libx264", "-f", "null", "-"
            ]
        });
        if (processResult.ExitCode == 0)
        {
            Model.CustomParameters.AddRange(
            [
                "-init_hw_device",
                "opencl=ocl",
                "-filter_hw_device",
                "ocl"
            ]);

            video.Filter.Add(
                "format=p010le,hwupload=derive_device=opencl,tonemap_opencl=tonemap=bt2390:transfer=smpte2084:matrix=bt2020:primaries=bt2020:format=p010le,hwdownload,format=p010le");
        }
        else
        {
            args.Logger?.WLog("Could not find openCL, you may want the oneVPL DockerMod");
            video.Filter.Add(
                "tonemapx=tonemap=bt2390:transfer=smpte2084:matrix=bt2020:primaries=bt2020"
            );
        }

        args.Logger?.WLog("QSV does not support dolby vision 5 decode properly so we are disabling it");
        Variables["NoQSV"] = true;

        return forceEncode;
    }
    
   
    /// <summary>
    /// Gets the encoder to use
    /// </summary>
    /// <param name="args">the node parameters</param>
    /// <returns>the encoder to use</returns>
    private string GetEncoder(NodeParameters args)
    {
        bool noNvidia =
            args.Variables.Any(x => x.Key?.ToLowerInvariant() == "nonvidia" && x.Value as bool? == true);
        bool noQsv =
            args.Variables.Any(x => x.Key?.ToLowerInvariant() == "noqsv" && x.Value as bool? == true);

        switch (Codec)
        {
            case "hevc":
            {
                if (noQsv == false && CanUseHardwareEncoding.CanProcess_Qsv_Hevc(args))
                    return "hevc_qsv";
                if (noNvidia == false && CanUseHardwareEncoding.CanProcess_Nvidia_Hevc(args))
                    return "hevc_nvenc";
                return "hevc";
            }
            case "h264":
            {
                if (noQsv == false && CanUseHardwareEncoding.CanProcess_Qsv_H264(args))
                    return "h264_qsv";
                if (noNvidia == false && CanUseHardwareEncoding.CanProcess_Nvidia_H264(args))
                    return "h264_nvenc";
                return "h264";
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

    /// <summary>
    /// Performs a CRF search encoding operation using the specified parameters and external tool.
    /// </summary>
    /// <param name="args">The node parameters containing context and utilities.</param>
    /// <param name="abAv1">The full path to the ab-av1 executable.</param>
    /// <param name="localFile">The path to the input video file.</param>
    /// <param name="targetCodec">The target codec to use for encoding (e.g., "hevc", "h264").</param>
    /// <param name="preset">The encoding preset to apply (e.g., "slow", "medium").</param>
    /// <param name="bitratePercent">The target bitrate percentage relative to the original video bitrate.</param>
    /// <param name="targetPercent">The target VMAF quality percentage to achieve.</param>
    /// <param name="videoBitrate">The bitrate of the source video in bits per second.</param>
    /// <param name="videoStream">The video stream metadata and settings.</param>
    /// <returns>
    /// A <see cref="CrfSearchResult"/> containing the results of the CRF search,
    /// including the best CRF score found, the full command used, and any errors encountered.
    /// </returns>
    private CrfSearchResult CrfSearch(NodeParameters args, string abAv1, string localFile, string targetCodec,
        string preset,
        int bitratePercent, int targetPercent, float videoBitrate, FfmpegVideoStream videoStream)

    {
        List<string> command = [targetCodec, "-preset", preset];
        command.AddRange(["-g", (videoStream.Stream.FramesPerSecond * 10).ToString(CultureInfo.InvariantCulture)]);

        if (targetCodec.Contains("qsv", StringComparison.InvariantCultureIgnoreCase))
            command.AddRange(["-look_ahead", "1", "-extbrc", "1", "-look_ahead_depth", "40"]);

        var videoPixelFormat = "yuv420p";

        if (videoStream.Stream.Is10Bit)
        {
            videoPixelFormat = "yuv420p10le";
            if (targetCodec.Contains("hevc", StringComparison.InvariantCultureIgnoreCase))
                command.AddRange(["-pix_fmt:v:0", "p010le", "-profile:v:0", "main10"]);
        }

        var targetBitRate = (bitratePercent / 100f) * videoBitrate;

        args.Logger?.ILog($"Searching for CRF under {GeneralHelper.HumanizeBitrate(targetBitRate)} @ {targetPercent}% original quality");


        var executeArgs = new ExecuteArgs();
        executeArgs.Command = abAv1;
        executeArgs.ArgumentList =
        [
            "crf-search",
            "-i",
            localFile,
            "--preset",
            preset,
            "-e",
            targetCodec,
            "--temp-dir",
            args.TempPath,
            "--min-vmaf",
            targetPercent.ToString(),
            "--max-encoded-percent",
            bitratePercent.ToString(),
            "--pix-format",
            videoPixelFormat,
            "--min-crf",
            "5",
            "--max-crf",
            "25",
            "--min-samples",
            "5",
            //"--sample-duration",
            //"5s",
        ];
        
        bool needsBtbnFfmpeg = executeArgs.ArgumentList.Any(arg =>
            arg.Contains("libvmaf", StringComparison.OrdinalIgnoreCase) ||
            arg.Contains("--min-vmaf", StringComparison.OrdinalIgnoreCase) ||
            targetCodec.Contains("libsvtav1", StringComparison.OrdinalIgnoreCase) ||
            targetCodec.Contains("libaom-av1", StringComparison.OrdinalIgnoreCase)
        );

        // Append both to the existing PATH
        string abAv1Path = new FileInfo(abAv1).Directory!.FullName;
        string ffmpegPath = new FileInfo(needsBtbnFfmpeg ? ffmpegBtbn : ffmpegJellyfin).Directory!.FullName;
        
        string? existingPath = Environment.GetEnvironmentVariable("PATH");
        string newPath = $"{abAv1Path}{Path.PathSeparator}{ffmpegPath}{Path.PathSeparator}{existingPath}";
        //string newPath = $"{ffmpegPath}{Path.PathSeparator}{existingPath}";
        executeArgs.EnvironmentalVariables["PATH"] = newPath;
        args.Logger?.ILog("New Path: " + newPath);

        var returnValue = new CrfSearchResult();
        returnValue.Command = command;

        executeArgs.Error += (line) =>
        {
            if (string.IsNullOrWhiteSpace(line))
                return;


            line = line.Substring(line.IndexOf(" ", StringComparison.Ordinal) + 1);

            Match match;

            match = Regex.Match(line, @"encoding sample (\d+)/(\d+).* crf (\d+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                args.AdditionalInfoRecorder("Sampling", $"CRF {match.Groups[3].Value}", 1, null);
                if (double.TryParse(match.Groups[1].Value, out double part) &&
                    double.TryParse(match.Groups[2].Value, out double total) && total != 0)
                {
                    float percent = (float)((100f / total) * part);
                    args.PartPercentageUpdate(percent);
                }

                return;
            }

            match = Regex.Match(line, @"eta (\d+) (seconds|minutes|hours|days|weeks)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                args.AdditionalInfoRecorder("ETA", $"{match.Groups[1].Value} {match.Groups[2].Value}", 1, null);
                return;
            }

            match = Regex.Match(line, @"crf ([0-9.]+) VMAF ([0-9.]+) predicted.*\(([0-9.]+)%", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                returnValue.Data.Add(new(
                    match.Groups[1].Value.Trim(),
                    match.Groups[2].Value.Trim(),
                    match.Groups[3].Value.Trim())
                );
            }

            match = Regex.Match(line, @"crf ([0-9.]+) successful", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string successfulCrf = match.Groups[1].Value;
                foreach (var entry in returnValue.Data)
                {
                    if (entry.Crf == successfulCrf)
                    {
                        returnValue.Winner = entry;
                        break;
                    }
                }
            }
        };

        var executeAbAv1 = args.Execute(executeArgs);

        if (executeAbAv1.ExitCode != 0)
        {
            if (executeAbAv1.Output.Contains("Failed to find a suitable crf",
                    StringComparison.InvariantCultureIgnoreCase))
            {
                returnValue.Message = "Failed to find a suitable crf";
                if (ErrorOnFail)
                {
                    returnValue.Error = true;
                }
            }
            else
            {
                args.Logger?.WLog(executeAbAv1.Output);
                returnValue.Message =
                    "Failed to execute ab-av1: " + executeAbAv1.ExitCode;
                returnValue.Error = true;
            }
        }

        args.Logger?.Table(returnValue.Data, "CRF Search Results", new[] { "Crf", "Score", "Size" });

        return returnValue;
    }

    /// <summary>
    /// Represents the result of a CRF search operation,
    /// including the command used, collected data points, the winning CRF entry,
    /// an error message, and an error flag.
    /// </summary>
    class CrfSearchResult
    {
        /// <summary>
        /// The command arguments used during the CRF search.
        /// </summary>
        public List<string> Command = new();

        /// <summary>
        /// The list of CRF score data collected during the search.
        /// </summary>
        public List<CrfScore> Data = new();

        /// <summary>
        /// The CRF score entry that was selected as the winner.
        /// </summary>
        public CrfScore Winner;

        /// <summary>
        /// An optional message, typically containing error or status information.
        /// </summary>
        public string Message;

        /// <summary>
        /// Indicates whether the CRF search operation resulted in an error.
        /// </summary>
        public bool Error;

        /// <summary>
        /// Creates a failed CRF search result with the specified error message.
        /// </summary>
        /// <param name="message">The error message describing the failure.</param>
        /// <returns>A <see cref="CrfSearchResult"/> instance representing failure.</returns>
        public static CrfSearchResult Failed(string message) => new CrfSearchResult
        {
            Message = message,
            Error = true,
            Data = new List<CrfScore>()
        };
    }


    /// <summary>
    /// Represents a single CRF (Constant Rate Factor) result entry,
    /// including the CRF value, predicted VMAF score, and encoded size percentage.
    /// </summary>
    /// <param name="Crf">The CRF value used during encoding.</param>
    /// <param name="Score">The predicted VMAF quality score.</param>
    /// <param name="Size">The encoded file size as a percentage of the original.</param>
    record CrfScore(string Crf, string Score, string Size);


}