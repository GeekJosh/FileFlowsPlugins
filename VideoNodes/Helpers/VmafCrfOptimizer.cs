using FileFlows.VideoNodes.FfmpegBuilderNodes.Models;

namespace FileFlows.VideoNodes.Helpers;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;

public class VmafResult
{
    public float Vmaf { get; set; }
    public float SizePercent { get; set; }
    public string Error { get; set; }
}

public class VmafCrfOptimizer
{
    private readonly string _encodeFFmpeg;
    private readonly string _vmafFFmpeg;
    private readonly ILogger _logger;
    private readonly string _tempDir;
    private readonly string _inputFile;
    private readonly NodeParameters _nodeParameters;
    private readonly float _fps;
    private readonly TimeSpan _duration;

    public event Action<float, float> CrfTesting;

    public VmafCrfOptimizer(NodeParameters args, string encodeFFmpeg, string vmafFfmpeg, string inputFile, float fps, TimeSpan duration)
    {
        _logger = args.Logger;
        _encodeFFmpeg = encodeFFmpeg;
        _vmafFFmpeg = vmafFfmpeg;
        _inputFile = inputFile;
        _tempDir = args.TempPath;
        _nodeParameters = args;
        _fps = fps;
        _duration = duration;
    }

    public List<string> ExtractChunks(TimeSpan chunkDuration, int numberOfChunks)
{
    var outputFiles = new List<string>();
    Directory.CreateDirectory(_tempDir);

    double videoSeconds = _duration.TotalSeconds;

    if (videoSeconds < 30)
    {
        // Very short: use full video as single chunk
        _logger?.ILog("📼 Video < 30s: extracting full video as single chunk.");
        return ExtractSingleChunk(TimeSpan.Zero, _duration, "chunk_1.mp4");
    }

    if (videoSeconds < 60)
    {
        // Short: extract 20s from middle, or less if video shorter
        var duration = TimeSpan.FromSeconds(Math.Min(20, videoSeconds));
        var start = TimeSpan.FromSeconds((videoSeconds - duration.TotalSeconds) / 2);
        _logger?.ILog("📼 Video < 60s: extracting centered 20s chunk.");
        return ExtractSingleChunk(start, duration, "chunk_1.mp4");
    }

    if (videoSeconds < 180)
    {
        // Medium: two chunks at 30% and 60%
        var first = TimeSpan.FromSeconds(videoSeconds * 0.3);
        var second = TimeSpan.FromSeconds(videoSeconds * 0.6);
        _logger?.ILog("📼 Video < 3min: extracting 2 strategic chunks.");

        ExtractChunk(first, chunkDuration, "chunk_1.mp4", outputFiles);
        ExtractChunk(second, chunkDuration, "chunk_2.mp4", outputFiles);
        return outputFiles;
    }

    // Standard: use 20%–80% logic
    var startRange = videoSeconds * 0.2;
    var endRange = videoSeconds * 0.8;
    var usableRange = endRange - startRange;
    double totalRequired = numberOfChunks * chunkDuration.TotalSeconds;

    if (usableRange < totalRequired)
    {
        _logger?.WLog("⚠️ Not enough space in 20%–80% range, falling back to full duration and single chunk.");
        return ExtractSingleChunk(TimeSpan.Zero, _duration, "chunk_1.mp4");
    }

    var spacing = usableRange / (numberOfChunks + 1);
    for (int i = 0; i < numberOfChunks; i++)
    {
        var start = TimeSpan.FromSeconds(startRange + spacing * (i + 1));
        var file = $"chunk_{i + 1}.mp4";
        ExtractChunk(start, chunkDuration, file, outputFiles);
    }

    return outputFiles;
}

    // Extracts a single chunk and returns the list
    private List<string> ExtractSingleChunk(TimeSpan start, TimeSpan duration, string fileName)
    {
        var outputFiles = new List<string>();
        ExtractChunk(start, duration, fileName, outputFiles);
        return outputFiles;
    }

    // Handles actual ffmpeg call and output check
    private void ExtractChunk(TimeSpan start, TimeSpan duration, string fileName, List<string> outputFiles)
    {
        var outputFile = Path.Combine(_tempDir, fileName);

        if (File.Exists(outputFile) && new FileInfo(outputFile).Length > 1000)
        {
            outputFiles.Add(outputFile);
            return;
        }

        _logger?.ILog($"✂️ Extracting chunk at {start:g} for {duration:g}");

        if (ExecuteProcess(new()
            {
                Command = _encodeFFmpeg,
                ArgumentList =
                [
                    "-hide_banner", "-y",
                    "-ss", start.TotalSeconds.ToString(CultureInfo.InvariantCulture),
                    "-i", _inputFile,
                    "-t", duration.TotalSeconds.ToString(CultureInfo.InvariantCulture),
                    "-map", "0:v:0",
                    "-c:v", "copy",
                    outputFile
                ]
            }).Failed(out var error))
        {
            _logger.ELog($"Failed extracting chunk: {error}");
            return;
        }

        if (File.Exists(outputFile))
            outputFiles.Add(outputFile);
    }

    private string GetCrfParameter(string encoder)
    {
        if (encoder.Contains("qsv"))
            return "-global_quality";
        if (encoder.Contains("nvenc"))
            return "-cq";
        if (encoder.Contains("vaapi"))
            return "-q";
        if (encoder.Contains("vulkan"))
            return "-qp";
        return "-crf";
    }

    public (float bestCrf, VmafResult bestResult, bool shouldReencode) FindBestCrf(
        string encoder,
        string pixelFormat,
        string preset,
        float minVmaf = 93,
        float crfStart = 10,
        float crfEnd = 24,
        float crfStep = 0.5f,
        int numberOfChunks = 5,
        int chunkSeconds = 20,
        int maxIterations = 5)
    {
        var chunks = ExtractChunks(TimeSpan.FromSeconds(chunkSeconds), numberOfChunks);
        if (chunks.Count == 0)
            throw new Exception("No chunks extracted.");

        crfStart = RoundToNearestStep(crfStart, crfStep);
        crfEnd = RoundToNearestStep(crfEnd, crfStep);

        VmafResult best = null;
        float bestCrf = -1;

        _logger?.ILog($"🔍 Trying highest CRF first: {crfEnd}");
        var topResult = TryCrf(chunks, encoder, pixelFormat, preset, crfEnd);
        if (topResult == null)
        {
            _logger?.ELog($"❌ Unable to evaluate highest CRF {crfEnd} — cannot continue.");
            return (-1, null, false);
        }

        if (topResult.Vmaf >= minVmaf)
        {
            _logger?.ILog($"✅ Highest CRF {crfEnd} met target VMAF {topResult.Vmaf:F2}, using this.");
            return (crfEnd, topResult, topResult.SizePercent < 100f);
        }

        _logger?.ILog(
            $"ℹ️ Highest CRF {crfEnd} did not meet VMAF target ({topResult.Vmaf:F2} < {minVmaf}), trying lowest CRF {crfStart}...");

        var lowResult = TryCrf(chunks, encoder, pixelFormat, preset, crfStart);
        if (lowResult == null)
        {
            _logger?.ELog($"❌ Unable to evaluate lowest CRF {crfStart} — cannot continue.");
            return (-1, null, false);
        }

        if (lowResult.Vmaf < minVmaf)
        {
            _logger?.WLog(
                $"❌ Even lowest CRF {crfStart} did not reach acceptable quality (VMAF {lowResult.Vmaf:F2} < {minVmaf}).");
            return (-1, lowResult, false);
        }

        _logger?.ILog($"🔁 Starting search between CRF {crfStart} and {crfEnd} to find acceptable balance.");

        float low = crfStart;
        float high = crfEnd;
        best = lowResult;
        bestCrf = crfStart;

        int iterations = 0;
        while (low + crfStep <= high && iterations < maxIterations)
        {
            float mid = RoundToStep((low + high) / 2f, crfStep);
            _logger?.ILog($"🔍 Testing CRF {mid} (iteration {iterations + 1})...");

            var result = TryCrf(chunks, encoder, pixelFormat, preset, mid);
            if (result == null)
            {
                _logger?.ILog($"⚠️ CRF {mid} could not be evaluated.");
                high = mid - crfStep;
            }
            else if (result.Vmaf >= minVmaf)
            {
                _logger?.ILog($"✅ CRF {mid} acceptable (VMAF {result.Vmaf:F2}) at {result.SizePercent:F2}% size.");
                best = result;
                bestCrf = mid;
                low = mid + crfStep;
            }
            else
            {
                _logger?.ILog($"ℹ️ CRF {mid} below VMAF target ({result.Vmaf:F2} < {minVmaf}). Trying higher quality.");
                high = mid - crfStep;
            }

            iterations++;
        }

        if (best != null)
        {
            _logger?.ILog($"🏁 Selected CRF: {bestCrf} with size {best.SizePercent:0.##}% and VMAF {best.Vmaf:0.##}");
        }
        else
        {
            _logger?.WLog("❌ No CRF value met the target quality within given bounds.");
        }

        bool shouldReencode = best != null && best.SizePercent < 100f;
        return (bestCrf, best, shouldReencode);
    }

    private string GetPixelFormat(FfmpegVideoStream videoStream, string encoder, out List<string> extraFilters)
    {
        extraFilters = new List<string>();

        string pixelFormat = encoder.Contains("qsv") ? "nv12" : "yuv420p";

        if (videoStream.Stream.Is10Bit)
        {
            pixelFormat = "yuv420p10le";

            if (encoder.Contains("hevc", StringComparison.InvariantCultureIgnoreCase))
            {
                extraFilters.Add("-pix_fmt:v:0");
                extraFilters.Add("p010le");
                extraFilters.Add("-profile:v:0");
                extraFilters.Add("main10");
            }
        }

        return pixelFormat;
    }

    /// <summary>
    /// Optimizes the encoding parameters of the video stream using VMAF to find the best CRF value
    /// for a given encoder and preset. If no acceptable CRF is found, encoding can be forced using the highest CRF.
    /// </summary>
    /// <param name="stream">The video stream to optimize.</param>
    /// <param name="encoder">The encoder name (e.g., hevc_qsv, h264_nvenc).</param>
    /// <param name="preset">The encoding speed preset (e.g., slow, fast).</param>
    /// <param name="forceEncoding">If true, forces encoding even if minimum VMAF is not met.</param>
    /// <param name="minVmaf">The minimum acceptable VMAF score.</param>
    /// <param name="crfStart">The starting CRF value for the search.</param>
    /// <param name="crfEnd">The ending CRF value for the search.</param>
    /// <param name="crfStep">The CRF step size used during binary search.</param>
    /// <param name="numberOfChunks">Number of chunks to sample from the video for VMAF analysis.</param>
    /// <param name="chunkSeconds">Duration in seconds of each chunk.</param>
    /// <param name="maxIterations">Maximum number of iterations in the binary search.</param>
    /// <returns>True if the stream was modified with optimized settings; otherwise, false.</returns>
    public bool Optimize(FfmpegVideoStream stream, string encoder, string preset,
        bool forceEncoding = false,
        float minVmaf = 93,
        float crfStart = 10,
        float crfEnd = 24,
        float crfStep = 0.5f,
        int numberOfChunks = 5,
        int chunkSeconds = 20,
        int maxIterations = 5)
    {
        string pixelFormat = GetPixelFormatAndUpdateStream(stream, encoder);
        
        crfStart = RoundToNearestStep(crfStart, crfStep);
        crfEnd = RoundToNearestStep(crfEnd, crfStep);
        
        _logger.ILog($@"[Optimize Settings]
  Encoder        : {encoder}
  Preset         : {preset}
  Force Encoding : {forceEncoding}
  Pixel Format   : {pixelFormat}
  Min VMAF       : {minVmaf}
  CRF Start      : {crfStart}
  CRF End        : {crfEnd}
  CRF Step       : {crfStep}
  Chunks         : {numberOfChunks}
  Chunk Seconds  : {chunkSeconds}
  Max Iterations : {maxIterations}
  Stream Info:
    - Codec      : {stream.Codec}
    - BitDepth   : {(stream.Stream.Is10Bit ? 10 : 8)}
    - Duration:  : {stream.Stream.Duration}
    - Resolution : {stream.Stream.Width}x{stream.Stream.Height}
");
        
        var (bestCrf, result, shouldReencode) = FindBestCrf(
            encoder, pixelFormat, preset,
            minVmaf, crfStart, crfEnd, crfStep,
            numberOfChunks, chunkSeconds, maxIterations
        );

        if (shouldReencode == false && forceEncoding == false)
        {
            _logger?.ILog("Encoding will not produce a smaller file, skipping encoding");
            return false;
        }

        if (bestCrf > 0 && result != null)
        {
            // ✅ Found a CRF that meets quality threshold
            int quality = (int)Math.Round(bestCrf);
            var parameters = GetEncodingParameters(stream, encoder, preset, quality);
            stream.EncodingParameters.Clear();
            stream.EncodingParameters.AddRange(parameters);
            return true;
        }

        if (forceEncoding)
        {
            _logger?.WLog("⚠️ Forcing re-encode using highest CRF fallback (quality threshold not met).");

            int fallbackQuality = (int)Math.Round(crfEnd);
            var parameters = GetEncodingParameters(stream, encoder, preset, fallbackQuality);
            stream.EncodingParameters.Clear();
            stream.EncodingParameters.AddRange(parameters);
            return true;
        }

        // 🚫 No encoding done
        return false;
    }
    
    /// <summary>
    /// Rounds the given value to the nearest multiple of the specified step size,
    /// rounding midpoint values (e.g., 0.25 with step 0.5) down rather than up.
    /// </summary>
    /// <param name="value">The value to round.</param>
    /// <param name="step">The step size to round to (e.g., 0.5).</param>
    /// <returns>The value rounded to the nearest multiple of the step.</returns>
    float RoundToNearestStep(float value, float step)
    {
        float exact = value / step;
        float rounded = (float)Math.Floor(exact + 0.5f - 1e-6f); // bias halfway values down
        return rounded * step;
    }
    
    /// <summary>
    /// Determines the pixel format and applies any necessary pix_fmt/profile filters to the stream.
    /// </summary>
    /// <param name="stream">The video stream being encoded.</param>
    /// <param name="encoder">The encoder being used.</param>
    /// <returns>The pixel format to use.</returns>
    private string GetPixelFormatAndUpdateStream(FfmpegVideoStream stream, string encoder)
    {
        var pixelFormat = encoder.Contains("qsv") ? "nv12" : "yuv420p";

        if (stream.Stream.Is10Bit)
        {
            pixelFormat = "yuv420p10le";

            if (encoder.Contains("hevc", StringComparison.InvariantCultureIgnoreCase))
            {
                stream.Filter.Add("-pix_fmt:v:0");
                stream.Filter.Add("p010le");
                stream.Filter.Add("-profile:v:0");
                stream.Filter.Add("main10");
            }
        }

        return pixelFormat;
    }

    /// <summary>
    /// Generates the appropriate encoding parameters for the specified encoder, preset, and CRF/quality value.
    /// </summary>
    /// <param name="stream">The video stream containing metadata like frame rate.</param>
    /// <param name="encoder">The encoder string (e.g., hevc_qsv, h264_nvenc).</param>
    /// <param name="preset">The encoder preset string.</param>
    /// <param name="quality">The CRF/quantizer quality value to use.</param>
    /// <returns>A list of encoding parameters suitable for the selected encoder.</returns>
    private List<string> GetEncodingParameters(FfmpegVideoStream stream, string encoder, string preset, int quality)
    {
        var fps = stream.Stream.FramesPerSecond;
        int gop = (int)Math.Round(fps * 5);
        string speed = preset ?? "slow";

        var list = new List<string>();

        if (encoder.Contains("qsv", StringComparison.InvariantCultureIgnoreCase))
        {
            list.Add(encoder);
            if (encoder.Contains("hevc"))
                list.AddRange(["-load_plugin", "hevc_hw"]);

            list.AddRange(new[]
            {
                "-global_quality", quality.ToString(),
                "-preset", speed,
                "-look_ahead", "1",
                "-look_ahead_depth", "40",
                "-g", gop.ToString(CultureInfo.InvariantCulture)
            });
        }
        else if (encoder.Contains("nvenc", StringComparison.InvariantCultureIgnoreCase))
        {
            list.Add(encoder);
            list.AddRange(new[]
            {
                "-cq", quality.ToString(),
                "-preset", speed,
                "-rc", "vbr",
                "-g", gop.ToString(CultureInfo.InvariantCulture)
            });
        }
        else if (encoder.Contains("vaapi", StringComparison.InvariantCultureIgnoreCase))
        {
            list.Add(encoder);
            list.AddRange(new[]
            {
                "-q", quality.ToString(),
                "-preset", speed,
                "-g", gop.ToString(CultureInfo.InvariantCulture)
            });
        }
        else if (encoder.Contains("amf", StringComparison.InvariantCultureIgnoreCase))
        {
            list.Add(encoder);
            list.AddRange(new[]
            {
                "-cq", quality.ToString(),
                "-preset", speed
            });
        }
        else if (encoder.Contains("vulkan", StringComparison.InvariantCultureIgnoreCase))
        {
            list.Add(encoder);
            list.AddRange(new[]
            {
                "-qp", quality.ToString(),
                "-preset", speed
            });
        }
        else if (encoder.Contains("aom", StringComparison.InvariantCultureIgnoreCase) ||
                 encoder.Contains("libaom", StringComparison.InvariantCultureIgnoreCase))
        {
            list.Add(encoder);
            list.AddRange(new[]
            {
                "-crf", quality.ToString(),
                "-b:v", "0",
                "-preset", speed
            });
        }
        else
        {
            // CPU encoders (libx264, libx265, etc.)
            list.Add(encoder);
            list.AddRange(new[]
            {
                "-crf", quality.ToString(),
                "-preset", speed
            });
        }

        return list;
    }

    private float RoundToStep(float value, float step)
    {
        return (float)(Math.Round(value / step) * step);
    }

    private VmafResult TryCrf(List<string> chunks, string encoder, string pixelFormat, string preset, float crf)
    {
        var results = new List<VmafResult>();

        for(int i=0; i<chunks.Count; i++)
        {
            var chunk =  chunks[i];
            CrfTesting?.Invoke(crf, i / ((float)chunks.Count));
            var r = ComputeVmaf(chunk, encoder, pixelFormat, crf, preset);
            if (!string.IsNullOrWhiteSpace(r.Error))
            {
                var reason = !string.IsNullOrWhiteSpace(r.Error) ? r.Error : $"VMAF too low ({r.Vmaf:0.##})";
                _logger?.WLog($"⚠️ CRF {crf} failed on chunk ({reason})");
                return null;
            }

            results.Add(r);
        }

        var avgSize = results.Average(r => r.SizePercent);
        var avgVmaf = results.Average(r => r.Vmaf);

        return new VmafResult { SizePercent = avgSize, Vmaf = avgVmaf };
    }

    
    public VmafResult ComputeVmaf(string original, string encoder, string pixelFormat, float crf, string preset)
    {
        var encoded = Path.Combine(_tempDir, Path.GetFileNameWithoutExtension(original) + $"_encoded_crf{crf}.mp4");
        var result = new VmafResult();

        try
        {
            if (ExecuteProcess(new()
                {
                    LogCommand = true,
                    Command = _encodeFFmpeg,
                    ArgumentList = GetArguments(original, encoded, encoder, crf, pixelFormat, preset)
                }).Failed(out var error))
                throw new Exception(error);

            string fpsStr = ((int)Math.Round(_fps)).ToString();

            string lavfi =
                $"[0:v]fps={fpsStr},scale=1920:1080:flags=bicubic,setpts=PTS-STARTPTS[dist];" +
                $"[1:v]fps={fpsStr},scale=1920:1080:flags=bicubic,setpts=PTS-STARTPTS[ref];" +
                "[dist][ref]libvmaf";

            var outputResult = ExecuteProcess(new()
            {
                Command = _vmafFFmpeg,
                ArgumentList =
                [
                    "-hide_banner",
                    "-i", encoded,
                    "-i", original,
                    "-lavfi", lavfi,
                    "-f", "null", "-"
                ]
            });
            if (outputResult.Failed(out error))
                throw new Exception(error);

            var output = outputResult.Value;

            var match = Regex.Match(output, @"VMAF score:\s*([0-9.]+)");
            if (match.Success && float.TryParse(match.Groups[1].Value, out float vmaf))
            {
                result.Vmaf = vmaf;
            }
            else
            {
                result.Error = "Failed to parse VMAF score from ffmpeg output.";
                _logger.ELog(result.Error);
            }

            var originalSize = new FileInfo(original).Length;
            var encodedSize = new FileInfo(encoded).Length;
            result.SizePercent = originalSize == 0 ? 0 : (encodedSize * 100f) / originalSize;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger?.ELog($"❌ ComputeVmaf failed: {ex.Message}");
        }

        return result;
    }

    private List<string> GetArguments(string inputFile, string outputFile, string encoder, float crf,
        string pixelFormat, string preset)
    {
        List<string> args;

        if (encoder.Contains("vaapi"))
        {
            args = new List<string>
            {
                "-hide_banner", "-y",
                "-hwaccel", "vaapi",
                "-vaapi_device", "/dev/dri/renderD128",
                "-i", inputFile,
                "-vf", "format=nv12,hwupload",
                "-c:v", encoder,
                GetCrfParameter(encoder), crf.ToString(CultureInfo.InvariantCulture),
                "-c:a", "copy",
                "-movflags", "+faststart",
                outputFile
            };
        }
        else
        {
            args = new List<string>
            {
                "-hide_banner", "-y",
                "-i", inputFile,
                "-c:v", encoder,
                GetCrfParameter(encoder), crf.ToString(CultureInfo.InvariantCulture),
                "-pix_fmt", pixelFormat,
                "-preset", preset,
                "-threads", "0",
                "-movflags", "+faststart",
                "-c:a", "copy",
                outputFile
            };
        }

        return args;
    }

    private Result<string> ExecuteProcess(ProcessParameters p)
    {
        string Escaped(string arg)
        {
            return arg.Contains(' ') || arg.Contains('"') || arg.Contains('\'')
                ? $"\"{arg.Replace("\"", "\\\"")}\""
                : arg;
        }

        var commandLine = $"{p.Command} {string.Join(" ", p.ArgumentList.Select(Escaped))}";
        if (p.LogCommand)
        {
            _logger?.ILog($"Executing: {commandLine}");
        }

        var result = _nodeParameters.Process.ExecuteShellCommand(new()
        {
            Command = p.Command,
            ArgumentList = p.ArgumentList.ToArray(),
            Silent = true
        }).GetAwaiter().GetResult();

        if (result.ExitCode != 0)
            return Result<string>.Fail($"Command failed: {commandLine}\n{result.Output}");

        return result.Output ?? string.Empty;
    }

    public class ProcessParameters
    {
        public string Command { get; set; }
        public List<string> ArgumentList { get; set; } = new();

        /// <summary>
        /// If true, logs the command before executing it.
        /// </summary>
        public bool LogCommand { get; set; } = false;
    }
}