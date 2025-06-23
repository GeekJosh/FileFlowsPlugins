using System.IO;

namespace FileFlows.VideoNodes.Helpers;

/// <summary>
/// Video Helper
/// </summary>
public class VideoHelper
{
    /// <summary>
    /// Determines the closest standard video resolution label based on width and height.
    /// </summary>
    /// <param name="width">The width of the video.</param>
    /// <param name="height">The height of the video.</param>
    /// <returns>
    /// A resolution label such as "SD", "720p", "1080p", or "4K".
    /// Returns <c>null</c> if the resolution does not match any standard.
    /// </returns>
    public static string FormatResolution(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        if (Approximately(width, 7680) || Approximately(height, 4320))
            return "8K";

        if (Approximately(width, 3840) || Approximately(height, 2160))
            return "4K";

        if (Approximately(width, 1920) || Approximately(height, 1080))
            return "1080p";

        if (Approximately(width, 1280) || Approximately(height, 720))
            return "720p";

        if (Approximately(width, 640) || Approximately(height, 360))
            return "360p";

        if (Approximately(width, 480) || Approximately(height, 360))
            return "480p";

        if (Approximately(width, 720) || Approximately(height, 480))
            return "SD";

        if (Approximately(width, 426) || Approximately(height, 240))
            return "240p";

        return null;

        static bool Approximately(int value, int target)
        {
            int tolerance = (int)(target * 0.1); // 10% tolerance
            return Math.Abs(value - target) <= tolerance;
        }
    }

    /// <summary>
    /// Retrieves the estimated bitrate of the video stream.
    /// </summary>
    /// <param name="args">The node parameters containing logging and other context.</param>
    /// <param name="videoInfo">The metadata information about the video and audio streams.</param>
    /// <param name="localFile">The local file path to the video file.</param>
    /// <returns>
    /// The estimated bitrate of the video stream in bits per second.
    /// Returns 0 if bitrate cannot be determined.
    /// </returns>
    public static float GetBitrate(NodeParameters args, VideoInfo videoInfo, string localFile)
    {
        var video = videoInfo.VideoStreams.FirstOrDefault(x => x.Bitrate > 0);

        if (video != null)
            return video.Bitrate;


        args.Logger?.ILog("Bitrate not found in metadata, calculating...");

        float GetEstimatedVideoBitrate(float totalBitrate)
        {
            if (videoInfo.AudioStreams?.Any() != true)
                return totalBitrate;

            foreach (var audio in videoInfo.AudioStreams)
                totalBitrate -= audio.Bitrate > 0 ? audio.Bitrate : totalBitrate * 0.05f;

            return Math.Max(0, totalBitrate);
        }

        if (videoInfo.Bitrate > 0)
            return GetEstimatedVideoBitrate(videoInfo.Bitrate);

        // Fallback to file size-based calculation
        var fileSize = new FileInfo(localFile).Length; // bytes
        var duration = videoInfo.VideoStreams[0].Duration;
        if (duration.TotalSeconds < 1)
        {
            args.Logger?.WLog("No duration available to calculate bitrate.");
            return 0;
        }

        var totalBitrate = (float)(fileSize * 8 / duration.TotalSeconds); // bits per second
        args.Logger?.ILog($"Calculated total bitrate from file size and duration: {totalBitrate} bps");

        return GetEstimatedVideoBitrate(totalBitrate);
    }

    /// <summary>
    /// Gets or sets the encoders options
    /// </summary>
    public static List<ListOption> Encoders => new()
    {
        new() { Label = "Automatic", Value = "" },
        new() { Label = "CPU", Value = "CPU" },
        new() { Label = "NVIDIA", Value = "NVIDIA" },
        new() { Label = "Intel QSV", Value = "Intel QSV" },
        new() { Label = "VAAPI", Value = "VAAPI" },
        new() { Label = "AMD AMF", Value = "AMD AMF" }
    };

    /// <summary>
    /// Gets or sets the encoders options with video toolbox
    /// </summary>
    public static List<ListOption> EncodersWithVideoToolbox => new()
    {
        new() { Label = "Automatic", Value = "" },
        new() { Label = "CPU", Value = "CPU" },
        new() { Label = "NVIDIA", Value = "NVIDIA" },
        new() { Label = "Intel QSV", Value = "Intel QSV" },
        new() { Label = "VAAPI", Value = "VAAPI" },
        new() { Label = "AMD AMF", Value = "AMD AMF" },
        new() { Label = "Mac Video Toolbox", Value = "Mac Video Toolbox" },
    };

    /// <summary>
    /// Parses an encoder
    /// </summary>
    /// <param name="encoder">the encoder string</param>
    /// <returns>the encoder to use</returns>
    internal static EncoderType ParseEncoder(string encoder)
    {
        if (string.IsNullOrWhiteSpace(encoder))
            return EncoderType.Automatic;

        encoder = encoder.Trim().ToLowerInvariant();

        return encoder switch
        {
            "cpu" => EncoderType.Cpu,
            "nvidia" => EncoderType.Nvidia,
            "intel qsv" or "qsv" => EncoderType.Qsv,
            "vaapi" => EncoderType.Vaapi,
            "amd amf" or "amd" or "amf" => EncoderType.Amf,
            "vulkan" => EncoderType.Vulkan,
            "mac video toolbox" or "videotoolbox" => EncoderType.VideoToolbox,
            _ => EncoderType.Automatic
        };
    }

    /// <summary>
    /// Gets the FFmpeg encoder to use based on codec and encoder preference
    /// </summary>
    /// <param name="args">The node parameters</param>
    /// <param name="encoder">The encoder preference as string (e.g. "NVIDIA", "CPU", etc.)</param>
    /// <param name="codec">The codec (e.g. "h264", "hevc", "av1")</param>
    /// <returns>The actual FFmpeg encoder to use</returns>
    internal static string GetEncoder(NodeParameters args, string encoder, string codec)
    {
        var enc = ParseEncoder(encoder);
        bool isAutomatic = enc == EncoderType.Automatic;

        // Determine hardware exclusions from args
        bool noNvidia = HasFlag(args, "nonvidia");
        bool noQsv = HasFlag(args, "noqsv");
        bool noVaapi = HasFlag(args, "novaapi");
        bool noAmf = HasFlag(args, "noamf") || HasFlag(args, "noamd");
        bool noVideoToolbox = HasFlag(args, "novideotoolbox");
        bool noVulkan = HasFlag(args, "novulkan");

        if (!isAutomatic)
        {
            return (codec, enc) switch
            {
                ("hevc", EncoderType.Qsv) => "hevc_qsv",
                ("hevc", EncoderType.Nvidia) => "hevc_nvenc",
                ("hevc", EncoderType.Amf) => "hevc_amf",
                ("hevc", EncoderType.Vulkan) => "hevc_vulkan",
                ("hevc", EncoderType.VideoToolbox) => "hevc_videotoolbox",
                ("hevc", EncoderType.Vaapi) => "hevc_vaapi",
                ("hevc", EncoderType.Cpu) => "libx265",

                ("h264", EncoderType.Qsv) => "h264_qsv",
                ("h264", EncoderType.Nvidia) => "h264_nvenc",
                ("h264", EncoderType.Amf) => "h264_amf",
                ("h264", EncoderType.Vulkan) => "h264_vulkan",
                ("h264", EncoderType.VideoToolbox) => "h264_videotoolbox",
                ("h264", EncoderType.Vaapi) => "h264_vaapi",
                ("h264", EncoderType.Cpu) => "libx264",

                ("av1", EncoderType.Qsv) => "av1_qsv",
                ("av1", EncoderType.Nvidia) => "av1_nvenc",
                ("av1", EncoderType.Cpu) => "libsvtav1",

                _ => codec
            };
        }

        // Automatic encoder logic
        switch (codec)
        {
            case "hevc":
                if (!noQsv && CanUseHardwareEncoding.CanProcess_Qsv_Hevc(args))
                    return "hevc_qsv";
                if (!noNvidia && CanUseHardwareEncoding.CanProcess_Nvidia_Hevc(args))
                    return "hevc_nvenc";
                if (!noAmf && CanUseHardwareEncoding.CanProcess_Amd_Hevc(args))
                    return "hevc_amf";
                if (!noVulkan && CanUseHardwareEncoding.CanProcess_Vulkan_Hevc(args))
                    return "hevc_vulkan";
                if (!noVideoToolbox && CanUseHardwareEncoding.CanProcess_VideoToolbox_Hevc(args))
                    return "hevc_videotoolbox";
                if (!noVaapi && CanUseHardwareEncoding.CanProcess_Vaapi_Hevc(args))
                    return "hevc_vaapi";
                return "libx265";

            case "h264":
                if (!noQsv && CanUseHardwareEncoding.CanProcess_Qsv_H264(args))
                    return "h264_qsv";
                if (!noNvidia && CanUseHardwareEncoding.CanProcess_Nvidia_H264(args))
                    return "h264_nvenc";
                if (!noAmf && CanUseHardwareEncoding.CanProcess_Amd_H264(args))
                    return "h264_amf";
                if (!noVulkan && CanUseHardwareEncoding.CanProcess_Vulkan_H264(args))
                    return "h264_vulkan";
                if (!noVideoToolbox && CanUseHardwareEncoding.CanProcess_VideoToolbox_H264(args))
                    return "h264_videotoolbox";
                if (!noVaapi && CanUseHardwareEncoding.CanProcess_Vaapi_H264(args))
                    return "h264_vaapi";
                return "libx264";

            case "av1":
                if (!noQsv && CanUseHardwareEncoding.CanProcess_Qsv_AV1(args))
                    return "av1_qsv";
                if (!noNvidia && CanUseHardwareEncoding.CanProcess_Nvidia_AV1(args))
                    return "av1_nvenc";
                return "libsvtav1";

            default:
                return codec;
        }
    }

    /// <summary>
    /// Checks if a variable is set to true in the args
    /// </summary>
    private static bool HasFlag(NodeParameters args, string key)
    {
        return args.Variables.Any(x =>
            string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase) &&
            x.Value is bool b && b);
    }


}



internal enum EncoderType
{
    Automatic,
    Cpu,
    Nvidia,
    Qsv,
    Vaapi,
    Amf,
    Vulkan,
    VideoToolbox
}