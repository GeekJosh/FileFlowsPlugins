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

        if(video != null)
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


}