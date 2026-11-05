using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;
using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace Jellyfin.Controller.Tests.MediaEncoding;

public class Av1HdrTranscodingTests
{
    [Theory]
    [InlineData("smpte2084", VideoRangeType.HDR10)]
    [InlineData("arib-std-b67", VideoRangeType.HLG)]
    public void HdrSource_SelectsMatchingRange(string transfer, VideoRangeType expected)
    {
        var state = State(transfer);
        Assert.Equal(expected, Av1HdrTranscoding.GetOutputRange(state, Options()));
    }

    [Fact]
    public void SdrSource_DoesNotEnterHdrPipeline()
    {
        Assert.Null(Av1HdrTranscoding.GetOutputRange(State("bt709"), Options()));
    }

    [Fact]
    public void DisabledOption_RestoresOriginalPipeline()
    {
        var options = Options();
        options.EnableAv1HdrTranscoding = false;
        Assert.Null(Av1HdrTranscoding.GetOutputRange(State(), options));
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("h264")]
    [InlineData("hevc")]
    public void OtherOutputCodecs_DoNotEnterAv1Pipeline(string codec)
    {
        var state = State();
        state.OutputVideoCodec = codec;
        Assert.Null(Av1HdrTranscoding.GetOutputRange(state, Options()));
    }

    [Fact]
    public void DolbyVisionWithoutCompatibleBaseLayer_IsNotReinterpretedAsHdr10()
    {
        var state = State();
        state.VideoStream.DvProfile = 5;
        state.VideoStream.DvBlSignalCompatibilityId = 0;
        state.VideoStream.RpuPresentFlag = 1;
        state.VideoStream.BlPresentFlag = 1;
        Assert.Throws<NotSupportedException>(() => Av1HdrTranscoding.GetOutputRange(state, Options()));
    }

    [Fact]
    public void NoSubtitles_StaysOnGpuAndPreservesTenBitPixels()
    {
        var graph = Helper().GetQsvAv1HdrFilters(State(), Options(), "hevc_vaapi", "av1_qsv", VideoRangeType.HDR10);
        Assert.Contains(graph.MainFilters, x => x.Contains("w=1920:h=1080", StringComparison.Ordinal));
        Assert.Contains(graph.MainFilters, x => x.Contains("format=p010le", StringComparison.Ordinal));
        Assert.Contains(graph.MainFilters, x => x.Contains("out_range=tv", StringComparison.Ordinal));
        Assert.Contains("hwmap=derive_device=qsv", graph.MainFilters);
        Assert.DoesNotContain("hwdownload", graph.MainFilters);
        Assert.DoesNotContain(graph.MainFilters, x => x.Contains("tonemap", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.MainFilters, x => x.Contains("procamp", StringComparison.Ordinal));
        Assert.Empty(graph.SubFilters);
        Assert.Empty(graph.OverlayFilters);
    }

    [Theory]
    [InlineData("hdmv_pgs_subtitle", false)]
    [InlineData("srt", true)]
    [InlineData("ass", true)]
    public void BurnIn_UsesTenBitCompositionAndHdrReferenceWhite(string codec, bool text)
    {
        var state = State(subtitleCodec: codec);
        var graph = Helper().GetQsvAv1HdrFilters(state, Options(), "hevc_vaapi", "av1_qsv", VideoRangeType.HDR10);
        Assert.Contains("hwdownload", graph.MainFilters);
        Assert.Contains("format=yuv420p10le", graph.MainFilters);
        var subtitle = string.Join(',', graph.SubFilters);
        Assert.Contains("t=smpte2084", subtitle, StringComparison.Ordinal);
        Assert.Contains("npl=203", subtitle, StringComparison.Ordinal);
        Assert.Contains("yuva420p10le:alpha_modes=straight", subtitle, StringComparison.Ordinal);
        Assert.Equal(text, subtitle.Contains("alpha_mode=premultiplied", StringComparison.Ordinal));
        Assert.Contains("alphaextract", subtitle, StringComparison.Ordinal);
        Assert.Contains("alphamerge", subtitle, StringComparison.Ordinal);
        Assert.Contains("overlay=eof_action=pass:repeatlast=0:format=yuv420p10:alpha=straight", graph.OverlayFilters);
        Assert.Contains("hwupload=derive_device=qsv:extra_hw_frames=24", graph.OverlayFilters);
        Assert.DoesNotContain(graph.OverlayFilters, x => x.Contains("overlay_qsv", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("hdmv_pgs_subtitle")]
    [InlineData("srt")]
    public void SdrFilterGraphs_AreIdenticalWithFeatureEnabledOrDisabled(string? subtitle)
    {
        var helper = Helper();
        var state = State("bt709", subtitle);
        var options = Options();
        options.EnableAv1HdrTranscoding = false;
        var before = helper.GetIntelQsvVaapiVidFiltersPrefered(state, options, "hevc_vaapi", "av1_qsv");
        options.EnableAv1HdrTranscoding = true;
        var after = helper.GetIntelQsvVaapiVidFiltersPrefered(state, options, "hevc_vaapi", "av1_qsv");
        Assert.Equal(before.MainFilters, after.MainFilters);
        Assert.Equal(before.SubFilters, after.SubFilters);
        Assert.Equal(before.OverlayFilters, after.OverlayFilters);
    }

    [Fact]
    public void InvalidDecoder_DoesNotSilentlyProduceEightBitHdr()
    {
        Assert.Throws<NotSupportedException>(() => Helper().GetQsvAv1HdrFilters(State(), Options(), string.Empty, "av1_qsv", VideoRangeType.HDR10));
    }

    [Fact]
    public void HdrEncoderArguments_PreserveStaticColorAndRemoveStaleDynamicMetadata()
    {
        var args = Helper().GetVideoQualityParam(State(), "av1_qsv", Options(), EncoderPreset.slow);
        Assert.Contains("-color_trc:v:0 smpte2084", args, StringComparison.Ordinal);
        Assert.Contains("-color_primaries:v:0 bt2020", args, StringComparison.Ordinal);
        Assert.Contains("-colorspace:v:0 bt2020nc", args, StringComparison.Ordinal);
        Assert.Contains("-color_range:v:0 tv", args, StringComparison.Ordinal);
        Assert.Contains("av1_metadata=remove_dovi=1:remove_hdr10plus=1", args, StringComparison.Ordinal);
    }

    [Fact]
    public void SdrEncoderArguments_AreUnchanged()
    {
        var state = State("bt709");
        var options = Options();
        var helper = Helper();
        options.EnableAv1HdrTranscoding = false;
        var before = helper.GetVideoQualityParam(state, "av1_qsv", options, EncoderPreset.slow);
        options.EnableAv1HdrTranscoding = true;
        var after = helper.GetVideoQualityParam(state, "av1_qsv", options, EncoderPreset.slow);
        Assert.Equal(before, after);
    }

    [Fact]
    public void ExportHardwareFixtures_WhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("JELLYFIN_HDR_FIXTURES");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var fixtures = new List<object>();
        foreach (var transfer in new[] { "smpte2084", "arib-std-b67" })
        {
            foreach (var subtitle in new string?[] { null, "hdmv_pgs_subtitle", "srt", "ass" })
            {
                var state = State(transfer, subtitle);
                state.VideoStream.Width = 1280;
                state.VideoStream.Height = 720;
                state.BaseRequest.MaxWidth = 640;
                state.BaseRequest.MaxHeight = 360;
                var range = Av1HdrTranscoding.GetOutputRange(state, Options())!.Value;
                var helper = Helper();
                var graph = helper.GetQsvAv1HdrFilters(state, Options(), "hevc_vaapi", "av1_qsv", range);
                fixtures.Add(new
                {
                    Transfer = transfer,
                    Subtitle = subtitle,
                    Main = graph.MainFilters,
                    Sub = graph.SubFilters,
                    Overlay = graph.OverlayFilters,
                    ColorArguments = helper.GetVideoQualityParam(state, "av1_qsv", Options(), EncoderPreset.slow)
                });
            }
        }

        File.WriteAllText(path, JsonSerializer.Serialize(fixtures));
    }

    private static EncodingOptions Options() => new()
    {
        HardwareAccelerationType = HardwareAccelerationType.qsv,
        AllowAv1Encoding = true,
        EnableHardwareEncoding = true,
        EnableAv1HdrTranscoding = true,
        EnableTonemapping = true,
        EnableVppTonemapping = true
    };

    private static EncodingJobInfo State(string transfer = "smpte2084", string? subtitleCodec = null)
    {
        var hdr = transfer != "bt709";
        var video = new MediaStream
        {
            Index = 0,
            Type = MediaStreamType.Video,
            Codec = "hevc",
            Profile = "Main 10",
            BitDepth = hdr ? 10 : 8,
            PixelFormat = hdr ? "yuv420p10le" : "yuv420p",
            ColorTransfer = transfer,
            ColorPrimaries = hdr ? "bt2020" : "bt709",
            ColorSpace = hdr ? "bt2020nc" : "bt709",
            ColorRange = "tv",
            Width = 3840,
            Height = 2160,
            RealFrameRate = 24
        };
        var streams = new List<MediaStream> { video };
        MediaStream? subtitle = null;
        if (subtitleCodec is not null)
        {
            subtitle = new MediaStream
            {
                Index = 1,
                Type = MediaStreamType.Subtitle,
                Codec = subtitleCodec,
                IsExternal = subtitleCodec is "srt" or "ass",
                Path = "/media/subtitles." + subtitleCodec,
                Width = 1920,
                Height = 1080
            };
            streams.Add(subtitle);
        }

        return new EncodingJobInfo(TranscodingJobType.Hls)
        {
            BaseRequest = new VideoRequestDto { MaxWidth = 1920, MaxHeight = 1080, SubtitleStreamIndex = subtitle?.Index },
            MediaSource = new MediaSourceInfo { Id = "test", Path = "/media/source.mkv", Container = "mkv", MediaStreams = streams },
            VideoStream = video,
            SubtitleStream = subtitle,
            OutputVideoCodec = "av1",
            OutputVideoBitrate = 8000000,
            SubtitleDeliveryMethod = subtitle is null ? SubtitleDeliveryMethod.Drop : SubtitleDeliveryMethod.Encode,
            IsVideoRequest = true,
            IsInputVideo = true
        };
    }

    private static EncodingHelper Helper()
    {
        var encoder = new Mock<IMediaEncoder>();
        encoder.SetupGet(x => x.EncoderVersion).Returns(new Version(8, 1, 3));
        encoder.Setup(x => x.EscapeSubtitleFilterPath(It.IsAny<string>())).Returns((string x) => x);
        return new EncodingHelper(
            Mock.Of<IApplicationPaths>(),
            encoder.Object,
            Mock.Of<ISubtitleEncoder>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<IConfigurationManager>(),
            Mock.Of<IPathManager>());
    }
}
