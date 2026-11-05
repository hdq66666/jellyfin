using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Api.Helpers;
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

public class DynamicHlsVariantTests
{
    [Theory]
    [InlineData("smpte2084", "PQ", "16")]
    [InlineData("arib-std-b67", "HLG", "18")]
    public void Av1HdrTranscode_AdvertisesCompleteColorAndLevel(string transfer, string range, string transferCode)
    {
        using var state = State("hevc", "av1", transfer);
        var playlist = Playlist(Helper(Options()), state);

        Assert.Equal(1, VariantCount(playlist));
        Assert.Contains($",VIDEO-RANGE={range},CODECS=\"av01.0.15M.10.0.110.09.{transferCode}.09.0\"", playlist, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("15")]
    [InlineData("19")]
    public void Av1HdrTranscode_AdvertisesTheEncodedLevelIndex(string? requestedLevel)
    {
        using var state = State("hevc", "av1");
        state.Request.Level = requestedLevel;
        var playlist = Playlist(Helper(Options()), state, "?VideoCodec=av1&Level=" + requestedLevel);

        Assert.Contains("CODECS=\"av01.0.15M.10.0.110.09.16.09.0\"", playlist, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Av1SdrTranscode_KeepsShortCodecString(bool hdrEnabled)
    {
        var options = Options();
        options.EnableAv1HdrTranscoding = hdrEnabled;
        using var state = State("hevc", "av1", "bt709");
        var playlist = Playlist(Helper(options), state);

        Assert.Equal(1, VariantCount(playlist));
        Assert.Contains(",VIDEO-RANGE=SDR,CODECS=\"av01.0.15M.08\"", playlist, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1920, 1080, 90, "1080x1920")]
    [InlineData(1920, 1080, -90, "1080x1920")]
    [InlineData(1921, 1081, 0, "1920x1080")]
    [InlineData(6000, 2000, 0, "4096x1364")]
    public void Av1HdrTranscode_AdvertisesActualOutputSize(int width, int height, int rotation, string expected)
    {
        using var state = State("hevc", "av1");
        state.VideoStream.Width = width;
        state.VideoStream.Height = height;
        state.VideoStream.Rotation = rotation;
        var playlist = Playlist(Helper(Options()), state);

        Assert.Contains(",RESOLUTION=" + expected, playlist, StringComparison.Ordinal);
    }

    [Fact]
    public void Av1HdrCopy_UsesSourceSizeForCopyAndOutputSizeForTranscode()
    {
        using var state = State("av1", "copy");
        state.VideoStream.Rotation = 90;
        var playlist = Playlist(Helper(Options()), state, "?VideoCodec=copy");
        var variants = playlist.Split('\n').Where(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)).ToArray();

        Assert.Equal(2, variants.Length);
        Assert.Contains(",RESOLUTION=1920x1080", variants[0], StringComparison.Ordinal);
        Assert.Contains(",RESOLUTION=1080x1920", variants[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Av1SdrTranscode_KeepsOriginalResolutionBehavior()
    {
        using var state = State("hevc", "av1", "bt709");
        state.VideoStream.Width = 1921;
        state.VideoStream.Height = 1081;
        state.VideoStream.Rotation = 90;
        var playlist = Playlist(Helper(Options()), state);

        Assert.Contains(",RESOLUTION=1921x1081", playlist, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 60f, false, "24")]
    [InlineData(30f, 60f, false, "30")]
    [InlineData(null, 20f, false, "20")]
    [InlineData(null, null, true, "48")]
    public void Av1HdrTranscode_AdvertisesOutputFrameRate(float? requestedRate, float? maximumRate, bool deinterlaceDouble, string expected)
    {
        var options = Options();
        options.DeinterlaceDoubleRate = deinterlaceDouble;
        using var state = State("hevc", "av1");
        state.Request.Framerate = requestedRate;
        state.Request.MaxFramerate = maximumRate;
        state.VideoStream.IsInterlaced = deinterlaceDouble;
        var playlist = Playlist(Helper(options), state);

        Assert.Contains(",FRAME-RATE=" + expected, playlist, StringComparison.Ordinal);
    }

    [Fact]
    public void Av1SdrTranscode_KeepsOriginalFrameRateBehavior()
    {
        using var state = State("hevc", "av1", "bt709");
        state.Request.MaxFramerate = 60;
        var playlist = Playlist(Helper(Options()), state);

        Assert.Contains(",FRAME-RATE=60", playlist, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportHdrHlsFixtures_WhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("JELLYFIN_HDR_HLS_FIXTURES");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var fixtures = new Dictionary<string, Dictionary<string, string>>();
        foreach (var transfer in new[] { "smpte2084", "arib-std-b67" })
        {
            using var state = State("hevc", "av1", transfer);
            var playlist = Playlist(Helper(Options()), state);
            var codec = Regex.Match(playlist, "(?:^|,)CODECS=\\\"([^\\\"]+)\\\"");
            var range = Regex.Match(playlist, "(?:^|,)VIDEO-RANGE=(PQ|HLG)(?:,|$)");
            Assert.True(codec.Success, playlist);
            Assert.True(range.Success, playlist);
            fixtures.Add(transfer, new Dictionary<string, string>
            {
                ["CodecString"] = codec.Groups[1].Value,
                ["VideoRange"] = range.Groups[1].Value
            });
        }

        File.WriteAllText(path, JsonSerializer.Serialize(fixtures));
    }

    [Theory]
    [InlineData("smpte2084", "PQ", "16")]
    [InlineData("arib-std-b67", "HLG", "18")]
    public void Av1HdrCopy_AdvertisesEligibleHdrTranscodeAlongsideOriginal(string transfer, string range, string transferCode)
    {
        using var state = State("av1", "COPY", transfer);
        var playlist = Playlist(Helper(Options()), state, "?VideoCodec=copy&Level=15");

        Assert.Equal(2, VariantCount(playlist));
        Assert.Contains($",VIDEO-RANGE={range},CODECS=\"av01.0.12M.10\"", playlist, StringComparison.Ordinal);
        Assert.Contains($",VIDEO-RANGE={range},CODECS=\"av01.0.15M.10.0.110.09.{transferCode}.09.0\"", playlist, StringComparison.Ordinal);
        Assert.Contains("main.m3u8?VideoCodec=copy&Level=15", playlist, StringComparison.Ordinal);
        Assert.Contains("VideoCodec=av1", playlist, StringComparison.Ordinal);
        Assert.Contains("AllowVideoStreamCopy=false", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain("VideoCodec=h264", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain("VideoCodec=hevc", playlist, StringComparison.Ordinal);
        Assert.Equal("COPY", state.OutputVideoCodec);
    }

    [Fact]
    public void DolbyVisionP10Copy_PreservesOriginalWithoutTranscodeVariant()
    {
        using var state = State("av1", "copy");
        state.Request.VideoRangeType = "DOVI";
        state.VideoStream.DvProfile = 10;
        state.VideoStream.DvLevel = 8;
        state.VideoStream.DvBlSignalCompatibilityId = 0;
        state.VideoStream.RpuPresentFlag = 1;
        state.VideoStream.BlPresentFlag = 1;
        var playlist = Playlist(Helper(Options()), state, "?VideoCodec=copy");

        Assert.Equal(2, VariantCount(playlist));
        Assert.Contains("CODECS=\"dav1.10.08\"", playlist, StringComparison.Ordinal);
        Assert.Contains("CODECS=\"av01.0.12M.10\"", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowVideoStreamCopy=false", playlist, StringComparison.Ordinal);
        Assert.Equal("copy", state.OutputVideoCodec);
    }

    [Theory]
    [InlineData("primaries")]
    [InlineData("matrix")]
    [InlineData("range")]
    [InlineData("bitdepth")]
    [InlineData("pixel-format")]
    [InlineData("missing-metadata")]
    public void UnsupportedHdrCopy_PreservesOriginalWithoutTranscodeVariant(string invalidField)
    {
        using var state = State("av1", "copy");
        switch (invalidField)
        {
            case "primaries":
                state.VideoStream.ColorPrimaries = "bt709";
                break;
            case "matrix":
                state.VideoStream.ColorSpace = "bt709";
                break;
            case "range":
                state.VideoStream.ColorRange = "pc";
                break;
            case "bitdepth":
                state.VideoStream.BitDepth = 8;
                state.VideoStream.PixelFormat = "yuv420p";
                break;
            case "pixel-format":
                state.VideoStream.PixelFormat = "yuv444p10le";
                break;
            case "missing-metadata":
                state.VideoStream.ColorPrimaries = null;
                break;
        }

        var playlist = Playlist(Helper(Options()), state, "?VideoCodec=copy");

        Assert.Equal(1, VariantCount(playlist));
        Assert.Contains("main.m3u8?VideoCodec=copy", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowVideoStreamCopy=false", playlist, StringComparison.Ordinal);
        Assert.Equal("copy", state.OutputVideoCodec);
    }

    [Theory]
    [InlineData("level")]
    [InlineData("profile")]
    [InlineData("size")]
    public void UnsupportedHdrOutput_PreservesOriginalWithoutTranscodeVariant(string invalidField)
    {
        using var state = State("av1", "copy");
        switch (invalidField)
        {
            case "level":
                state.Request.Level = "8";
                break;
            case "profile":
                state.Request.Profile = "high";
                break;
            case "size":
                state.VideoStream.Width = 7680;
                state.VideoStream.Height = 4320;
                break;
        }

        var playlist = Playlist(Helper(Options()), state, "?VideoCodec=copy");

        Assert.Equal(1, VariantCount(playlist));
        Assert.Contains("main.m3u8?VideoCodec=copy", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowVideoStreamCopy=false", playlist, StringComparison.Ordinal);
        Assert.Equal("copy", state.OutputVideoCodec);
    }

    [Theory]
    [InlineData("smpte2084", "HLG", "PQ", "16")]
    [InlineData("arib-std-b67", "HDR10", "HLG", "18")]
    public void Av1HdrTranscode_PreservesSourceTransferDespiteRequestedRange(string transfer, string requestedRange, string outputRange, string transferCode)
    {
        using var state = State("hevc", "av1", transfer);
        state.Request.VideoRangeType = requestedRange;
        var playlist = Playlist(Helper(Options()), state);

        Assert.Contains($",VIDEO-RANGE={outputRange},CODECS=\"av01.0.15M.10.0.110.09.{transferCode}.09.0\"", playlist, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HardwareAccelerationType.qsv, true, true, true, 2, true, false)]
    [InlineData(HardwareAccelerationType.qsv, true, true, false, 3, true, true)]
    [InlineData(HardwareAccelerationType.qsv, false, true, true, 3, true, true)]
    [InlineData(HardwareAccelerationType.qsv, true, false, true, 2, false, true)]
    [InlineData(HardwareAccelerationType.vaapi, true, true, true, 3, true, true)]
    public void Av1HdrCopy_UsesAllConfigurationGates(
        HardwareAccelerationType acceleration,
        bool hardwareEncoding,
        bool allowAv1,
        bool hdrEnabled,
        int expectedVariants,
        bool expectAv1,
        bool expectH264)
    {
        var options = Options();
        options.HardwareAccelerationType = acceleration;
        options.EnableHardwareEncoding = hardwareEncoding;
        options.AllowAv1Encoding = allowAv1;
        options.EnableAv1HdrTranscoding = hdrEnabled;
        using var state = State("av1", "copy");
        var playlist = Playlist(Helper(options), state, "?VideoCodec=copy");

        Assert.Equal(expectedVariants, VariantCount(playlist));
        Assert.Equal(expectAv1, playlist.Contains("VideoCodec=av1", StringComparison.Ordinal));
        Assert.Equal(expectH264, playlist.Contains("VideoCodec=h264", StringComparison.Ordinal));
        Assert.DoesNotContain("VideoCodec=hevc", playlist, StringComparison.Ordinal);
        if (acceleration == HardwareAccelerationType.qsv && hardwareEncoding && allowAv1 && hdrEnabled)
        {
            Assert.Contains("av01.0.15M.10.0.110.09.16.09.0", playlist, StringComparison.Ordinal);
        }
        else if (expectAv1)
        {
            Assert.Contains("av01.0.15M.08", playlist, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 3)]
    public void HevcHdrCopy_SuppressesSdrVariantsOnlyWhenHdrModeIsEnabled(bool hdrEnabled, int expectedVariants)
    {
        var options = Options();
        options.EnableAv1HdrTranscoding = hdrEnabled;
        using var state = State("hevc", "copy");
        var playlist = Playlist(Helper(options), state, "?VideoCodec=copy");

        Assert.Equal(expectedVariants, VariantCount(playlist));
        Assert.Equal(!hdrEnabled, playlist.Contains("VideoCodec=hevc", StringComparison.Ordinal));
        Assert.Equal(!hdrEnabled, playlist.Contains("VideoCodec=h264", StringComparison.Ordinal));
        if (!hdrEnabled)
        {
            Assert.Contains(",VIDEO-RANGE=SDR", playlist, StringComparison.Ordinal);
            var h264Url = playlist.Split('\n').Single(line => line.Contains("VideoCodec=h264", StringComparison.Ordinal));
            Assert.DoesNotContain("hevc-profile", h264Url, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StaticHdrCopy_DoesNotAdvertiseAnUnusableTranscode()
    {
        using var state = State("av1", "copy");
        state.Request.Static = true;
        var playlist = Playlist(Helper(Options()), state, "?VideoCodec=copy&Static=true");

        Assert.Equal(1, VariantCount(playlist));
        Assert.DoesNotContain("AllowVideoStreamCopy=false", playlist, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Av1HdrCopy_RequiresNativeHardwareDecoding(bool preferNative, bool av1DecodingEnabled)
    {
        var options = Options();
        options.PreferSystemNativeHwDecoder = preferNative;
        options.HardwareDecodingCodecs = av1DecodingEnabled ? ["av1"] : [];
        using var state = State("av1", "copy");
        var playlist = Playlist(Helper(options), state, "?VideoCodec=copy");

        Assert.Equal(1, VariantCount(playlist));
        Assert.Contains("main.m3u8?VideoCodec=copy", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowVideoStreamCopy=false", playlist, StringComparison.Ordinal);
    }

    [Fact]
    public void Av1HdrCopy_WithMissingDecoderListPreservesOriginal()
    {
        var options = Options();
        options.HardwareDecodingCodecs = null;
        using var state = State("av1", "copy");
        var playlist = Playlist(Helper(options), state, "?VideoCodec=copy");

        Assert.Equal(1, VariantCount(playlist));
        Assert.Contains("main.m3u8?VideoCodec=copy", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowVideoStreamCopy=false", playlist, StringComparison.Ordinal);
        Assert.Equal("copy", state.OutputVideoCodec);
    }

    [Fact]
    public void FallbackFailure_RestoresOriginalCodec()
    {
        var options = Options();
        var config = new Mock<IServerConfigurationManager>();
        config.SetupSequence(x => x.GetConfiguration("encoding"))
            .Returns(options)
            .Throws(new InvalidOperationException("test failure"));
        var hls = Helper(options, config);
        using var state = State("av1", "COPY");
        var builder = new StringBuilder();

        Assert.Throws<InvalidOperationException>(() => hls.AppendPrimaryPlaylists(builder, state, "main.m3u8", "?VideoCodec=copy", 8000000, null));
        Assert.Contains("main.m3u8?VideoCodec=copy", builder.ToString(), StringComparison.Ordinal);
        Assert.Equal("COPY", state.OutputVideoCodec);
    }

    private static int VariantCount(string playlist)
        => playlist.Split("#EXT-X-STREAM-INF:", StringSplitOptions.None).Length - 1;

    private static string Playlist(DynamicHlsHelper helper, StreamState state, string query = "?VideoCodec=av1&Level=15")
    {
        var builder = new StringBuilder();
        helper.AppendPrimaryPlaylists(builder, state, "main.m3u8", query, 8000000, null);
        return builder.ToString();
    }

    private static EncodingOptions Options() => new()
    {
        HardwareAccelerationType = HardwareAccelerationType.qsv,
        EnableAv1HdrTranscoding = true,
        EnableHardwareEncoding = true,
        AllowAv1Encoding = true,
        AllowHevcEncoding = true,
        PreferSystemNativeHwDecoder = true,
        HardwareDecodingCodecs = ["av1", "hevc"]
    };

    private static StreamState State(string sourceCodec, string outputCodec, string transfer = "smpte2084")
    {
        var hdr = transfer != "bt709";
        return new StreamState(Mock.Of<IMediaSourceManager>(), TranscodingJobType.Hls, Mock.Of<ITranscodeManager>())
        {
            Request = new VideoRequestDto { Profile = "main", Level = "15" },
            MediaSource = new MediaSourceInfo(),
            OutputVideoCodec = outputCodec,
            OutputVideoBitrate = 8000000,
            VideoStream = new MediaStream
            {
                Type = MediaStreamType.Video,
                Codec = sourceCodec,
                Profile = "Main",
                Level = sourceCodec == "av1" ? 12 : 153,
                BitDepth = hdr ? 10 : 8,
                PixelFormat = hdr ? "yuv420p10le" : "yuv420p",
                ColorRange = "tv",
                ColorPrimaries = hdr ? "bt2020" : "bt709",
                ColorTransfer = transfer,
                ColorSpace = hdr ? "bt2020nc" : "bt709",
                Width = 1920,
                Height = 1080,
                RealFrameRate = 24
            }
        };
    }

    private static DynamicHlsHelper Helper(EncodingOptions options, Mock<IServerConfigurationManager>? config = null)
    {
        if (config is null)
        {
            config = new Mock<IServerConfigurationManager>();
            config.Setup(x => x.GetConfiguration("encoding")).Returns(options);
        }

        var media = Mock.Of<IMediaEncoder>();
        var helper = new EncodingHelper(
            Mock.Of<IApplicationPaths>(),
            media,
            Mock.Of<ISubtitleEncoder>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<IConfigurationManager>(),
            Mock.Of<IPathManager>());
        return new DynamicHlsHelper(
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
    }
}
