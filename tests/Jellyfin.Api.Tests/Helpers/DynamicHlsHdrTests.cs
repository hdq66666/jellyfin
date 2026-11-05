using System;
using System.Reflection;
using System.Text;
using Jellyfin.Api.Helpers;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace Jellyfin.Api.Tests.Helpers;

public class DynamicHlsHdrTests
{
    [Theory]
    [InlineData("smpte2084", true, ",VIDEO-RANGE=PQ", "10")]
    [InlineData("arib-std-b67", true, ",VIDEO-RANGE=HLG", "10")]
    [InlineData("bt709", true, ",VIDEO-RANGE=SDR", "08")]
    [InlineData("bt709", false, ",VIDEO-RANGE=SDR", "08")]
    [InlineData("smpte2084", false, ",VIDEO-RANGE=SDR", "08")]
    public void Manifest_DescribesActualAv1BitDepthAndTransfer(string transfer, bool enabled, string range, string depth)
    {
        var options = new EncodingOptions
        {
            HardwareAccelerationType = HardwareAccelerationType.qsv,
            EnableAv1HdrTranscoding = enabled,
            EnableHardwareEncoding = true,
            AllowAv1Encoding = true
        };
        var config = new Mock<IServerConfigurationManager>();
        config.Setup(x => x.GetConfiguration("encoding")).Returns(options);
        var media = Mock.Of<IMediaEncoder>();
        var helper = new EncodingHelper(
            Mock.Of<IApplicationPaths>(),
            media,
            Mock.Of<ISubtitleEncoder>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<IConfigurationManager>(),
            Mock.Of<IPathManager>());
        var hls = new DynamicHlsHelper(
            Mock.Of<ILibraryManager>(),
            Mock.Of<IUserManager>(),
            Mock.Of<IMediaSourceManager>(),
            config.Object,
            media,
            Mock.Of<ITranscodeManager>(),
            Mock.Of<INetworkManager>(),
            NullLogger<DynamicHlsHelper>.Instance,
            Mock.Of<IHttpContextAccessor>(),
            helper,
            Mock.Of<ITrickplayManager>());
        using var state = new StreamState(Mock.Of<IMediaSourceManager>(), TranscodingJobType.Hls, Mock.Of<ITranscodeManager>())
        {
            Request = new VideoRequestDto { Profile = "main" },
            MediaSource = new MediaSourceInfo(),
            OutputVideoCodec = "av1",
            VideoStream = new MediaStream
            {
                Type = MediaStreamType.Video,
                Codec = "hevc",
                BitDepth = 10,
                ColorPrimaries = transfer == "bt709" ? "bt709" : "bt2020",
                ColorTransfer = transfer,
                ColorSpace = transfer == "bt709" ? "bt709" : "bt2020nc"
            }
        };
        var buffer = new StringBuilder();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(DynamicHlsHelper).GetMethod("AppendPlaylistVideoRangeField", flags)!.Invoke(hls, [buffer, state]);
        var codec = (string)typeof(DynamicHlsHelper).GetMethod("GetPlaylistVideoCodecs", flags)!.Invoke(hls, [state, "av1", 53])!;
        Assert.Equal(range, buffer.ToString());
        Assert.StartsWith("av01.0.", codec, StringComparison.Ordinal);
        Assert.EndsWith("." + depth, codec, StringComparison.Ordinal);
    }
}
