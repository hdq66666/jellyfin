using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;
using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace Jellyfin.Api.Tests.Helpers;

public class StreamingHdrValidationTests
{
    [Fact]
    public async Task DolbyVisionP10_StreamCopyIsAllowed()
    {
        var sourceManager = new Mock<IMediaSourceManager>();
        using var state = await GetStreamingState(sourceManager, allowCopy: true, dovi: true, enableDecoder: true);

        Assert.Equal("copy", state.OutputVideoCodec);
        Assert.Equal(VideoRangeType.DOVI, state.VideoStream.VideoRangeType);
    }

    [Fact]
    public async Task DolbyVisionP10_ExplicitTranscodeIsRejectedAndClosesSource()
    {
        var sourceManager = new Mock<IMediaSourceManager>();
        sourceManager.Setup(x => x.CloseLiveStream("test-live-stream")).Returns(Task.CompletedTask);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => GetStreamingState(sourceManager, allowCopy: false, dovi: true, enableDecoder: true, requiresClosing: true));

        Assert.Contains("compatible base layer", error.Message, StringComparison.Ordinal);
        sourceManager.Verify(x => x.CloseLiveStream("test-live-stream"), Times.Once);
    }

    [Fact]
    public async Task ValidHdr_ExplicitTranscodeIsAllowed()
    {
        var sourceManager = new Mock<IMediaSourceManager>();
        using var state = await GetStreamingState(sourceManager, allowCopy: false, dovi: false, enableDecoder: true);

        Assert.Equal("av1", state.OutputVideoCodec);
        Assert.Equal(VideoRangeType.HDR10, state.VideoStream.VideoRangeType);
    }

    [Fact]
    public async Task DisabledDecoder_ExplicitTranscodeIsRejected()
    {
        var sourceManager = new Mock<IMediaSourceManager>();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => GetStreamingState(sourceManager, allowCopy: false, dovi: false, enableDecoder: false));

        Assert.Contains("hardware decoding", error.Message, StringComparison.Ordinal);
    }

    private static Task<StreamState> GetStreamingState(
        Mock<IMediaSourceManager> sourceManager,
        bool allowCopy,
        bool dovi,
        bool enableDecoder,
        bool requiresClosing = false)
    {
        var id = Guid.NewGuid();
        var video = new MediaStream
        {
            Type = MediaStreamType.Video,
            Codec = "av1",
            Profile = "Main",
            Level = 12,
            BitDepth = 10,
            PixelFormat = "yuv420p10le",
            ColorRange = "tv",
            ColorPrimaries = "bt2020",
            ColorTransfer = "smpte2084",
            ColorSpace = "bt2020nc",
            Width = 1920,
            Height = 1080,
            RealFrameRate = 24,
            BitRate = 8000000,
            DvProfile = dovi ? 10 : null,
            DvLevel = dovi ? 8 : null,
            DvBlSignalCompatibilityId = dovi ? 0 : null,
            RpuPresentFlag = dovi ? 1 : null,
            BlPresentFlag = dovi ? 1 : null
        };
        var mediaSource = new MediaSourceInfo
        {
            Id = id.ToString("N"),
            Path = "/media/video.mkv",
            Container = "mkv",
            RequiresClosing = requiresClosing,
            LiveStreamId = requiresClosing ? "test-live-stream" : null,
            MediaStreams = [video]
        };
        var item = new Video { Id = id };
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetItemById<BaseItem>(id)).Returns(item);
        sourceManager.Setup(x => x.GetPlaybackMediaSources(item, null, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([mediaSource]);
        var options = new EncodingOptions
        {
            HardwareAccelerationType = HardwareAccelerationType.qsv,
            EnableAv1HdrTranscoding = true,
            EnableHardwareEncoding = true,
            AllowAv1Encoding = true,
            PreferSystemNativeHwDecoder = true,
            HardwareDecodingCodecs = enableDecoder ? ["av1"] : [],
            TranscodingTempPath = "/tmp"
        };
        var paths = Mock.Of<IApplicationPaths>();
        var config = new Mock<IServerConfigurationManager>();
        config.Setup(x => x.GetConfiguration("encoding")).Returns(options);
        config.SetupGet(x => x.CommonApplicationPaths).Returns(paths);
        var mediaEncoder = Mock.Of<IMediaEncoder>();
        var encodingHelper = new EncodingHelper(
            paths,
            mediaEncoder,
            Mock.Of<ISubtitleEncoder>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<IConfigurationManager>(),
            Mock.Of<IPathManager>());
        var request = new VideoRequestDto
        {
            Id = id,
            VideoCodec = "av1",
            AudioCodec = "aac",
            Container = "mp4",
            AllowVideoStreamCopy = allowCopy,
            VideoRangeType = dovi ? "DOVI" : "HDR10",
            Level = "15",
            Profile = "main",
            VideoBitRate = 8000000
        };
        var context = new DefaultHttpContext();
        context.Request.Path = "/video/master.m3u8";
        return StreamingHelpers.GetStreamingState(
            request,
            context,
            sourceManager.Object,
            Mock.Of<IUserManager>(),
            library.Object,
            config.Object,
            mediaEncoder,
            encodingHelper,
            Mock.Of<ITranscodeManager>(),
            TranscodingJobType.Hls,
            CancellationToken.None);
    }
}
