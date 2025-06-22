namespace FileFlows.VideoNodes.Helpers;

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly string _ffmpeg;
    private readonly ILogger _logger;
    private readonly string _tempDir;
    private readonly string _inputFile;
    private readonly NodeParameters _nodeParameters;
    private readonly float _fps;
    private readonly TimeSpan _duration;

    public VmafCrfOptimizer(NodeParameters args, string ffmpeg, string inputFile, float fps, TimeSpan duration)
    {
        _logger = args.Logger;
        _ffmpeg = ffmpeg;
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


        // Calculate the usable time range (between 20% and 80%)
        var startRange = _duration.TotalSeconds * 0.2;
        var endRange = _duration.TotalSeconds * 0.8;
        var usableRange = endRange - startRange;

        if (usableRange < numberOfChunks * chunkDuration.TotalSeconds)
        {
            _logger?.WLog("⚠️ Not enough space to place all chunks in the 20%-80% range.");
            return outputFiles;
        }

        var spacing = usableRange / (numberOfChunks + 1);

        for (int i = 0; i < numberOfChunks; i++)
        {
            var start = TimeSpan.FromSeconds(startRange + spacing * (i + 1));
            var outputFile = Path.Combine(_tempDir, $"chunk_{i + 1}.mp4");

            if (File.Exists(outputFile) && new FileInfo(outputFile).Length > 1000)
            {
                outputFiles.Add(outputFile);
                continue;
            }

            _logger?.ILog($"✂️ Extracting chunk {i + 1} at {start}");

            ExecuteProcess(new()
            {
                Command = _ffmpeg,
                ArgumentList =
                [
                    "-hide_banner", "-y",
                    "-ss", start.TotalSeconds.ToString(CultureInfo.InvariantCulture),
                    "-i", _inputFile,
                    "-t", chunkDuration.TotalSeconds.ToString(CultureInfo.InvariantCulture),
                    "-map", "0:v:0",
                    "-c:v", "copy",
                    outputFile
                ]
            });

            if (File.Exists(outputFile))
                outputFiles.Add(outputFile);
        }

        return outputFiles;
    }

    public VmafResult ComputeVmaf(string original, string encoder, string pixelFormat, float crf, string preset)
    {
        var encoded = Path.Combine(_tempDir, Path.GetFileNameWithoutExtension(original) + "_encoded.mp4");
        var vmafLog = Path.Combine(_tempDir, Path.GetFileNameWithoutExtension(original) + "_vmaf.json");

        var result = new VmafResult();

        try
        {
            string crfArgument = GetCrfParameter(encoder);
            ExecuteProcess(new()
            {
                LogCommand = true,
                Command = _ffmpeg,
                ArgumentList =
                [
                    "-hide_banner", "-y",
                    "-i", original,
                    "-c:v", encoder,
                    crfArgument, crf.ToString(CultureInfo.InvariantCulture),
                    "-pix_fmt", pixelFormat,
                    "-preset", preset,
                    encoded
                ]
            });
            
            
            string fpsStr = ((int)Math.Round(_fps)).ToString(); // or keep full precision if you want

            string lavfi =
                $"[0:v]fps={fpsStr},scale=1920:1080:flags=bicubic,setpts=PTS-STARTPTS[dist];" +
                $"[1:v]fps={fpsStr},scale=1920:1080:flags=bicubic,setpts=PTS-STARTPTS[ref];" +
                "[dist][ref]libvmaf";

            var output = ExecuteProcess(new()
            {
                Command = _ffmpeg,
                ArgumentList =
                [
                    "-hide_banner",
                    "-i", encoded,
                    "-i", original,
                    "-lavfi", lavfi,
                    "-f", "null", "-"
                ]
            });

            // Parse VMAF score from output (e.g. "VMAF score: 57.955239")
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
        int crfStep = 1,
        int numberOfChunks = 6,
        int chunkSeconds = 30)
    {
        var chunks = ExtractChunks(TimeSpan.FromSeconds(chunkSeconds), numberOfChunks);
        float bestCrf = -1;
        VmafResult best = null;

        for (float crf = crfStart; crf <= crfEnd; crf += crfStep)
        {
            _logger.ILog($"\n🔍 Testing CRF {crf}...");

            var results = new List<VmafResult>();
            bool vmafTooLow = false;

            foreach (var chunk in chunks)
            {
                var r = ComputeVmaf(chunk, encoder, pixelFormat, crf, preset);
                if (!string.IsNullOrWhiteSpace(r.Error) || r.Vmaf < minVmaf)
                {
                    var reason = !string.IsNullOrWhiteSpace(r.Error)
                        ? r.Error
                        : $"VMAF too low ({r.Vmaf:0.##})";
                    _logger?.WLog($"⚠️ CRF {crf} failed on chunk ({reason})");

                    vmafTooLow = true;
                    break;
                }

                results.Add(r);
            }

            if (vmafTooLow)
            {
                // All higher CRFs will be worse quality — stop checking
                // break;
            }

            if (results.Count == chunks.Count)
            {
                var avgSize = results.Average(r => r.SizePercent);
                var avgVmaf = results.Average(r => r.Vmaf);

                _logger.ILog($"✅ CRF {crf} passed. Avg VMAF: {avgVmaf:F2}, Avg Size: {avgSize:F2}%");

                if (best == null || avgSize < best.SizePercent)
                {
                    bestCrf = crf;
                    best = new VmafResult { Vmaf = avgVmaf, SizePercent = avgSize };
                }
            }
        }


        // Get original file size to determine if re-encoding makes sense
        var shouldReencode = best != null && (best.SizePercent < 100f);
        
        if (best != null)
        {
            _logger.ILog($"🏁 Best CRF: {bestCrf} with {best.SizePercent:0.##}% size and {best.Vmaf:0.##} VMAF");
            if (!shouldReencode)
                _logger?.ILog("🚫 Best CRF result is larger than original — skipping re-encode.");
        }
        else
        {
            _logger.WLog("❌ No CRF setting produced acceptable results.");
        }
        

        return (bestCrf, best, shouldReencode);
    }


    private string ExecuteProcess(ProcessParameters p)
    {
        if (p.LogCommand)
        {
            string Escaped(string arg)
            {
                return arg.Contains(' ') || arg.Contains('"') || arg.Contains('\'')
                    ? $"\"{arg.Replace("\"", "\\\"")}\""
                    : arg;
            }

            var commandLine = $"{p.Command} {string.Join(" ", p.ArgumentList.Select(Escaped))}";
            _logger?.ILog($"Executing: {commandLine}");
        }

        var result = _nodeParameters.Process.ExecuteShellCommand(new()
        {
            Command = p.Command,
            ArgumentList = p.ArgumentList.ToArray(),
            Silent = true
        }).GetAwaiter().GetResult();

        if (p.ThrowOnError && result.ExitCode != 0)
            throw new Exception($"Command failed: {p.Command} {string.Join(" ", p.ArgumentList)}");

        return result.Output ?? string.Empty;
    }

    public class ProcessParameters
    {
        public string Command { get; set; }
        public List<string> ArgumentList { get; set; } = new();
        public bool ThrowOnError { get; set; } = true;
        /// <summary>
        /// If true, logs the command before executing it.
        /// </summary>
        public bool LogCommand { get; set; } = false;
    }
}
