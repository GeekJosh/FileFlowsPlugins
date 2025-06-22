#if(DEBUG)

using FileFlows.VideoNodes.FfmpegBuilderNodes;
using FileFlows.VideoNodes.FfmpegBuilderNodes.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VideoNodes.Tests;

namespace FileFlows.VideoNodes.Tests.FfmpegBuilderTests;

[TestClass]
[TestCategory("Slow")]
public class FFmpegBuilder_VideoEncodeAutoCrfTests: VideoTestBase
{
    
    NodeParameters args;

    /// <summary>
    /// Sets up the test environment before each test.
    /// Initializes video parameters and executes the video file setup.
    /// </summary>
    private void InitVideo(string file)
    {
        args = GetVideoNodeParameters(file);
        VideoFile vf = new VideoFile();
        vf.PreExecute(args);
        vf.Execute(args);

        FfmpegBuilderStart ffStart = new();
        ffStart.PreExecute(args);
        Assert.AreEqual(1, ffStart.Execute(args));
    }


    /// <summary>
    /// Crf
    /// </summary>
    [TestMethod]
    public void CrfTest1()
    {
        var file = "/home/john/Videos/unprocessed/Big-Movie.mkv";
        InitVideo(file);//VideoMkv);

        var ffmpegCrfEncode = new FfmpegBuilderVideoEncodeAutoCrfCustom()
        {
            Codec = "hevc"
        };

        ffmpegCrfEncode.PreExecute(args);
        int result = ffmpegCrfEncode.Execute(args);

        Assert.AreEqual(1, result);
    }
}

#endif