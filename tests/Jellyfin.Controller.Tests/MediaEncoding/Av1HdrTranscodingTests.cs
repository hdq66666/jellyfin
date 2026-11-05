using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
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
        var state = State("bt709");
        Assert.Null(Av1HdrTranscoding.GetOutputRange(state, Options()));
        Av1HdrTranscoding.ValidateTranscode(state, Options());
    }

    [Fact]
    public void DisabledOption_RestoresOriginalPipeline()
    {
        var options = Options();
        options.EnableAv1HdrTranscoding = false;
        var state = State();
        Assert.Null(Av1HdrTranscoding.GetOutputRange(state, options));
        Av1HdrTranscoding.ValidateTranscode(state, options);
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
        Av1HdrTranscoding.ValidateTranscode(state, Options());
    }

    [Fact]
    public void DolbyVisionWithoutCompatibleBaseLayer_IsNotReinterpretedAsHdr10()
    {
        var state = State();
        state.VideoStream.DvProfile = 5;
        state.VideoStream.DvBlSignalCompatibilityId = 0;
        state.VideoStream.RpuPresentFlag = 1;
        state.VideoStream.BlPresentFlag = 1;
        Assert.Null(Av1HdrTranscoding.GetSourceRange(state.VideoStream));
        Assert.Null(Av1HdrTranscoding.GetOutputRange(state, Options()));
        Assert.Throws<ArgumentException>(() => Av1HdrTranscoding.ValidateTranscode(state, Options()));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public void DolbyVisionCopy_DoesNotInvokeTranscodeValidation(int profile)
    {
        var state = State();
        state.VideoStream.Codec = "av1";
        state.VideoStream.DvProfile = profile;
        state.VideoStream.DvBlSignalCompatibilityId = 0;
        state.VideoStream.RpuPresentFlag = 1;
        state.VideoStream.BlPresentFlag = 1;
        state.OutputVideoCodec = "copy";

        Assert.False(Av1HdrTranscoding.IsRequested(state, Options()));
        Assert.Null(Av1HdrTranscoding.GetOutputRange(state, Options()));
        Av1HdrTranscoding.ValidateTranscode(state, Options());
        _ = Helper().GetVideoQualityParam(state, "copy", Options(), EncoderPreset.slow);
    }

    [Fact]
    public void StaticRequest_DoesNotInvokeTranscodeValidation()
    {
        var state = State();
        state.VideoStream.ColorPrimaries = null;
        state.BaseRequest.Static = true;

        Assert.False(Av1HdrTranscoding.IsRequested(state, Options()));
        Assert.Null(Av1HdrTranscoding.GetOutputRange(state, Options()));
        Av1HdrTranscoding.ValidateTranscode(state, Options());
    }

    [Theory]
    [InlineData("primaries")]
    [InlineData("matrix")]
    [InlineData("range")]
    [InlineData("pixel-format")]
    [InlineData("depth")]
    public void InvalidHdrSource_IsIneligibleAndExplicitTranscodeIsRejected(string field)
    {
        var state = State();
        switch (field)
        {
            case "primaries":
                state.VideoStream.ColorPrimaries = null;
                break;
            case "matrix":
                state.VideoStream.ColorSpace = "bt709";
                break;
            case "range":
                state.VideoStream.ColorRange = "pc";
                break;
            case "pixel-format":
                state.VideoStream.PixelFormat = "yuv420p";
                break;
            case "depth":
                state.VideoStream.BitDepth = 8;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }

        Assert.Null(Av1HdrTranscoding.GetSourceRange(state.VideoStream));
        Assert.Null(Av1HdrTranscoding.GetOutputRange(state, Options()));
        Assert.Throws<ArgumentException>(() => Av1HdrTranscoding.ValidateTranscode(state, Options()));
    }

    [Fact]
    public void NullSource_IsIneligibleWithoutThrowing()
    {
        Assert.Null(Av1HdrTranscoding.GetSourceRange(null));
    }

    [Theory]
    [InlineData("smpte2084", 1, VideoRangeType.HDR10)]
    [InlineData("arib-std-b67", 4, VideoRangeType.HLG)]
    public void DolbyVisionCompatibleBaseLayer_UsesItsHdrTransfer(string transfer, int compatibilityId, VideoRangeType expected)
    {
        var state = State(transfer);
        state.VideoStream.DvProfile = 8;
        state.VideoStream.DvBlSignalCompatibilityId = compatibilityId;
        state.VideoStream.RpuPresentFlag = 1;
        state.VideoStream.BlPresentFlag = 1;

        Assert.Equal(expected, Av1HdrTranscoding.GetSourceRange(state.VideoStream));
        Assert.Equal(expected, Av1HdrTranscoding.GetOutputRange(state, Options()));
        Av1HdrTranscoding.ValidateTranscode(state, Options());
    }

    [Theory]
    [InlineData("smpte2084", 4)]
    [InlineData("arib-std-b67", 1)]
    public void DolbyVisionBaseLayerWithConflictingTransfer_IsRejected(string transfer, int compatibilityId)
    {
        var state = State(transfer);
        state.VideoStream.DvProfile = 8;
        state.VideoStream.DvBlSignalCompatibilityId = compatibilityId;
        state.VideoStream.RpuPresentFlag = 1;
        state.VideoStream.BlPresentFlag = 1;

        Assert.Null(Av1HdrTranscoding.GetSourceRange(state.VideoStream));
        Assert.Null(Av1HdrTranscoding.GetOutputRange(state, Options()));
        Assert.Throws<ArgumentException>(() => Av1HdrTranscoding.ValidateTranscode(state, Options()));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("codec")]
    [InlineData("null-codecs")]
    [InlineData("depth")]
    public void MissingHardwareDecoderPrerequisite_IsRejected(string prerequisite)
    {
        var options = Options();
        switch (prerequisite)
        {
            case "native":
                options.PreferSystemNativeHwDecoder = false;
                break;
            case "codec":
                options.HardwareDecodingCodecs = ["av1"];
                break;
            case "null-codecs":
                options.HardwareDecodingCodecs = null;
                break;
            case "depth":
                options.EnableDecodingColorDepth10Hevc = false;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(prerequisite));
        }

        var state = State();
        Assert.Null(Av1HdrTranscoding.GetOutputRange(state, options));
        Assert.Throws<ArgumentException>(() => Av1HdrTranscoding.ValidateTranscode(state, options));
    }

    [Theory]
    [InlineData("main", "15", true)]
    [InlineData("0", "16", true)]
    [InlineData("high", "15", false)]
    [InlineData("main", "14", false)]
    [InlineData("main", "invalid", false)]
    public void ClientProfileAndLevel_AreValidated(string profile, string level, bool accepted)
    {
        var state = State();
        state.BaseRequest.Profile = profile;
        state.BaseRequest.Level = level;

        if (accepted)
        {
            Assert.NotNull(Av1HdrTranscoding.GetOutputRange(state, Options()));
            Av1HdrTranscoding.ValidateTranscode(state, Options());
        }
        else
        {
            Assert.Null(Av1HdrTranscoding.GetOutputRange(state, Options()));
            Assert.Throws<ArgumentException>(() => Av1HdrTranscoding.ValidateTranscode(state, Options()));
        }
    }

    [Fact]
    public void ProfileList_CannotProduceAnOutputOutsideMainProfile()
    {
        var state = State();
        state.BaseRequest.Profile = "high,main";
        var options = Options();

        if (Av1HdrTranscoding.GetOutputRange(state, options).HasValue)
        {
            var args = Helper().GetVideoQualityParam(state, "av1_qsv", options, EncoderPreset.slow);
            Assert.Matches(@"(?i) -profile:v:0 (main|0)(\s|$)", args);
            Assert.DoesNotContain(" -profile:v:0 high", args, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => Av1HdrTranscoding.ValidateTranscode(state, options));
        }
    }

    [Theory]
    [InlineData("smpte2084", "HLG", VideoRangeType.HDR10)]
    [InlineData("smpte2084", "SDR", VideoRangeType.HDR10)]
    [InlineData("arib-std-b67", "HDR10", VideoRangeType.HLG)]
    public void RequestedRange_DoesNotOverrideTheSourceHdrTransfer(string transfer, string requested, VideoRangeType expected)
    {
        var state = State(transfer);
        state.BaseRequest.VideoRangeType = requested;

        Assert.Equal(expected, Av1HdrTranscoding.GetOutputRange(state, Options()));
        Av1HdrTranscoding.ValidateTranscode(state, Options());
    }

    [Theory]
    [InlineData(SubtitleDeliveryMethod.Embed)]
    [InlineData(SubtitleDeliveryMethod.External)]
    [InlineData(SubtitleDeliveryMethod.Hls)]
    [InlineData(SubtitleDeliveryMethod.Drop)]
    public void NonBurnedSubtitles_DoNotBlockHdrTranscode(SubtitleDeliveryMethod delivery)
    {
        var state = State(subtitleCodec: "ass");
        state.SubtitleDeliveryMethod = delivery;

        Assert.Equal(VideoRangeType.HDR10, Av1HdrTranscoding.GetOutputRange(state, Options()));
        Av1HdrTranscoding.ValidateTranscode(state, Options());
    }

    [Theory]
    [InlineData(4096, 2176, 120, true)]
    [InlineData(4096, 2176, 121, false)]
    [InlineData(8192, 1088, 30, true)]
    [InlineData(8194, 1088, 30, true)]
    [InlineData(2048, 4352, 30, true)]
    [InlineData(2048, 4354, 30, true)]
    [InlineData(4096, 2178, 30, false)]
    public void OutputDimensionsAndDisplayRate_AreBoundedByLevel53(int width, int height, float fps, bool accepted)
    {
        var state = State();
        state.VideoStream.Width = width;
        state.VideoStream.Height = height;
        state.VideoStream.RealFrameRate = fps;
        state.BaseRequest.MaxWidth = null;
        state.BaseRequest.MaxHeight = null;

        Assert.Equal(accepted, Av1HdrTranscoding.GetOutputRange(state, Options()).HasValue);
        if (accepted)
        {
            Av1HdrTranscoding.ValidateTranscode(state, Options());
        }
        else
        {
            Assert.Throws<ArgumentException>(() => Av1HdrTranscoding.ValidateTranscode(state, Options()));
        }
    }

    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    public void RotatedOutputDimensions_AreBoundedByLevel53(int rotation)
    {
        var state = State();
        state.VideoStream.Width = 3000;
        state.VideoStream.Height = 4000;
        state.VideoStream.Rotation = rotation;
        state.BaseRequest.MaxWidth = 4096;
        state.BaseRequest.MaxHeight = 3000;

        Assert.Null(Av1HdrTranscoding.GetOutputRange(state, Options()));
        Assert.Throws<ArgumentException>(() => Av1HdrTranscoding.ValidateTranscode(state, Options()));
    }

    [Theory]
    [InlineData(3840, 2160, 0, 1920, 1080, 1920, 1080)]
    [InlineData(3000, 4000, 90, 4096, 3000, 4000, 3000)]
    [InlineData(3000, 4000, -90, 4096, 3000, 4000, 3000)]
    public void OutputSize_MatchesHardwareRotationAndScaling(
        int width,
        int height,
        int rotation,
        int maxWidth,
        int maxHeight,
        int expectedWidth,
        int expectedHeight)
    {
        var state = State();
        state.VideoStream.Width = width;
        state.VideoStream.Height = height;
        state.VideoStream.Rotation = rotation;
        state.BaseRequest.MaxWidth = maxWidth;
        state.BaseRequest.MaxHeight = maxHeight;

        var size = Av1HdrTranscoding.GetOutputSize(state);
        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
    }

    [Fact]
    public void OutputSize_InvalidSourceDimensionsAreUnknown()
    {
        var state = State();
        state.VideoStream.Width = 0;

        var size = Av1HdrTranscoding.GetOutputSize(state);
        Assert.Null(size.Width);
        Assert.Null(size.Height);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(60000001)]
    public void MissingOrOutOfRangeBitrate_IsRejected(int? bitrate)
    {
        var state = State();
        state.OutputVideoBitrate = bitrate;

        Assert.Null(Av1HdrTranscoding.GetOutputRange(state, Options()));
        Assert.Throws<ArgumentException>(() => Av1HdrTranscoding.ValidateTranscode(state, Options()));
    }

    [Fact]
    public void Level53MainTierBitrateLimit_IsAccepted()
    {
        var state = State();
        state.OutputVideoBitrate = 60000000;

        Assert.Equal(VideoRangeType.HDR10, Av1HdrTranscoding.GetOutputRange(state, Options()));
        Av1HdrTranscoding.ValidateTranscode(state, Options());
        var args = Helper().GetVideoQualityParam(state, "av1_qsv", Options(), EncoderPreset.slow);
        Assert.Contains(" -b:v 60000000 -maxrate 60000000 -rc_init_occupancy 60000000 -bufsize 60000000", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MaximumFrameRateNotAppliedToVfrInput_DoesNotUndercountSampleRate()
    {
        var state = State();
        state.VideoStream.Width = 4096;
        state.VideoStream.Height = 2176;
        state.VideoStream.AverageFrameRate = 24;
        state.VideoStream.RealFrameRate = 240;
        state.BaseRequest.MaxWidth = null;
        state.BaseRequest.MaxHeight = null;
        state.BaseRequest.MaxFramerate = 120;

        Assert.Equal(240, Av1HdrTranscoding.GetOutputFrameRate(state, Options()));
        Assert.Null(Av1HdrTranscoding.GetOutputRange(state, Options()));
        Assert.Throws<ArgumentException>(() => Av1HdrTranscoding.ValidateTranscode(state, Options()));
    }

    [Fact]
    public void AppliedFrameRateLimit_BoundsTheOutputSampleRate()
    {
        var state = State();
        state.VideoStream.Width = 4096;
        state.VideoStream.Height = 2176;
        state.VideoStream.AverageFrameRate = 240;
        state.VideoStream.RealFrameRate = 240;
        state.BaseRequest.MaxWidth = null;
        state.BaseRequest.MaxHeight = null;
        state.BaseRequest.MaxFramerate = 120;

        Assert.Equal(120, Av1HdrTranscoding.GetOutputFrameRate(state, Options()));
        Assert.Equal(VideoRangeType.HDR10, Av1HdrTranscoding.GetOutputRange(state, Options()));
        Av1HdrTranscoding.ValidateTranscode(state, Options());
    }

    [Fact]
    public void UnrealisticAverageFrameRate_UsesTheRealFrameRate()
    {
        var state = State();
        state.VideoStream.Width = 4096;
        state.VideoStream.Height = 2176;
        state.VideoStream.AverageFrameRate = 1000;
        state.VideoStream.RealFrameRate = 24;
        state.BaseRequest.MaxWidth = null;
        state.BaseRequest.MaxHeight = null;

        Assert.Equal(24, Av1HdrTranscoding.GetOutputFrameRate(state, Options()));
        Assert.Equal(VideoRangeType.HDR10, Av1HdrTranscoding.GetOutputRange(state, Options()));
        Av1HdrTranscoding.ValidateTranscode(state, Options());
    }

    [Fact]
    public void InterlacedDoubleRate_IsIncludedInOutputFrameRate()
    {
        var state = State();
        state.VideoStream.IsInterlaced = true;
        var options = Options();
        options.DeinterlaceDoubleRate = true;

        Assert.Equal(48, Av1HdrTranscoding.GetOutputFrameRate(state, options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidRequestedFrameRate_IsUnknown(float rate)
    {
        var state = State();
        state.BaseRequest.Framerate = rate;

        Assert.Null(Av1HdrTranscoding.GetOutputFrameRate(state, Options()));
        Assert.Null(Av1HdrTranscoding.GetOutputRange(state, Options()));
        Assert.Throws<ArgumentException>(() => Av1HdrTranscoding.ValidateTranscode(state, Options()));
    }

    [Theory]
    [InlineData(VideoRangeType.HDR10, "smpte2084")]
    [InlineData(VideoRangeType.HLG, "arib-std-b67")]
    public void SubtitleCompositor_RequiresLinearBlendAndMatchingTransfer(VideoRangeType range, string transfer)
    {
        var filter = Av1HdrTranscoding.GetSubtitleCompositionFilter(range);

        Assert.Contains("libplacebo=inputs=2:format=p010le", filter, StringComparison.Ordinal);
        Assert.Contains("colorspace=bt2020nc:color_primaries=bt2020", filter, StringComparison.Ordinal);
        Assert.Contains("color_trc=" + transfer, filter, StringComparison.Ordinal);
        Assert.Contains("range=tv", filter, StringComparison.Ordinal);
        Assert.Contains("peak_detect=0", filter, StringComparison.Ordinal);
        Assert.Contains("apply_dolbyvision=0:apply_filmgrain=0", filter, StringComparison.Ordinal);
        Assert.Contains("tonemapping=clip:gamut_mode=clip:contrast_recovery=0:dithering=none", filter, StringComparison.Ordinal);
        Assert.Contains("require_linear_blend=1", filter, StringComparison.Ordinal);
        Assert.Equal(range == VideoRangeType.HLG, filter.Contains("hlg_reference_peak=1000", StringComparison.Ordinal));
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
    [InlineData("smpte2084", "hdmv_pgs_subtitle", false)]
    [InlineData("smpte2084", "srt", true)]
    [InlineData("smpte2084", "ass", true)]
    [InlineData("arib-std-b67", "hdmv_pgs_subtitle", false)]
    [InlineData("arib-std-b67", "srt", true)]
    [InlineData("arib-std-b67", "ass", true)]
    public void BurnIn_UsesLinearGpuCompositionWithVideoDerivedClock(string transfer, string codec, bool text)
    {
        var state = State(transfer, codec);
        var range = Av1HdrTranscoding.GetSourceRange(state.VideoStream)!.Value;
        var helper = Helper();
        var graph = helper.GetQsvAv1HdrFilters(state, Options(), "hevc_vaapi", "av1_qsv", range);
        var main = string.Join(',', graph.MainFilters);
        var subtitle = string.Join(',', graph.SubFilters);
        var filterArguments = helper.FormatVideoFilterGraph(state, graph.MainFilters, graph.SubFilters, graph.OverlayFilters, helper.UsesHdrPgsCache(state, Options()));

        var timebaseIndex = main.IndexOf("settb=AVTB", StringComparison.Ordinal);
        var splitIndex = main.IndexOf("split=2[av1hdr_video][av1hdr_clock]", StringComparison.Ordinal);
        Assert.True(timebaseIndex >= 0 && splitIndex > timebaseIndex, "The shared time base must be set before splitting the subtitle clock.");
        Assert.Contains("[av1hdr_clock]scale_vaapi=w=16:h=16:format=nv12,hwdownload", main, StringComparison.Ordinal);
        Assert.Contains("lutrgb=r=0:g=0:b=0:a=0", main, StringComparison.Ordinal);
        Assert.Contains("[av1hdr_canvas];[av1hdr_video]hwmap=derive_device=drm:mode=read+direct", main, StringComparison.Ordinal);
        Assert.Contains("format=drm_prime", graph.MainFilters);
        Assert.Contains("setparams=colorspace=gbr:color_primaries=bt709:color_trc=bt709:range=pc:alpha_mode=straight", subtitle, StringComparison.Ordinal);
        Assert.DoesNotContain("alphamerge", subtitle, StringComparison.Ordinal);
        Assert.DoesNotContain("alphasrc", subtitle, StringComparison.Ordinal);

        if (text)
        {
            Assert.Contains("[av1hdr_canvas]setparams=alpha_mode=straight,subtitles=", subtitle, StringComparison.Ordinal);
            Assert.Contains(":alpha=1:sub2video=1:sub2video_skip=0", subtitle, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("[av1hdr_canvas][av1hdr_pgs]overlay=eof_action=pass:repeatlast=0:format=rgb:alpha=straight", subtitle, StringComparison.Ordinal);
        }

        Assert.Equal(Av1HdrTranscoding.GetSubtitleCompositionFilter(range), graph.OverlayFilters[0]);
        Assert.Equal("format=vulkan", graph.OverlayFilters[1]);
        Assert.Equal("hwmap=derive_device=vaapi:mode=read+direct", graph.OverlayFilters[2]);
        Assert.Equal("format=vaapi", graph.OverlayFilters[3]);
        Assert.Equal("hwmap=derive_device=qsv", graph.OverlayFilters[4]);
        Assert.Equal("format=qsv", graph.OverlayFilters[5]);
        Assert.Contains(" -filter_complex \"", filterArguments, StringComparison.Ordinal);
        Assert.Contains("[0:0]", filterArguments, StringComparison.Ordinal);
        Assert.Contains("[main][sub]" + Av1HdrTranscoding.GetSubtitleCompositionFilter(range), filterArguments, StringComparison.Ordinal);
        Assert.Contains("[av1hdr_canvas]", filterArguments, StringComparison.Ordinal);
        if (!text)
        {
            Assert.Contains("[1:0]", filterArguments, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void BurnIn_RequiresLibplaceboAndVulkan(bool libplacebo, bool vulkan)
    {
        var state = State(subtitleCodec: "ass");
        var helper = Helper(libplacebo, vulkan);

        Assert.Throws<NotSupportedException>(() => helper.GetQsvAv1HdrFilters(state, Options(), "hevc_vaapi", "av1_qsv", VideoRangeType.HDR10));
    }

    [Theory]
    [InlineData(FilterOptionType.LibplaceboRequireLinearBlend, VideoRangeType.HDR10, "hdmv_pgs_subtitle")]
    [InlineData(FilterOptionType.LibplaceboHlgReferencePeak, VideoRangeType.HLG, "hdmv_pgs_subtitle")]
    [InlineData(FilterOptionType.SubtitlesSub2videoSkip, VideoRangeType.HDR10, "ass")]
    public void BurnIn_RequiresPatchedFilterOptions(FilterOptionType missing, VideoRangeType range, string codec)
    {
        var state = State(range == VideoRangeType.HLG ? "arib-std-b67" : "smpte2084", codec);
        var helper = Helper(unsupportedOption: missing);

        Assert.Throws<NotSupportedException>(() => helper.GetQsvAv1HdrFilters(state, Options(), "hevc_vaapi", "av1_qsv", range));
    }

    [Theory]
    [InlineData("smpte2084", 0L)]
    [InlineData("smpte2084", 123456789L)]
    [InlineData("arib-std-b67", 0L)]
    [InlineData("arib-std-b67", 123456789L)]
    public void InternalHdrPgsBurnIn_UsesUnseekedCacheAfterMainInput(string transfer, long startTicks)
    {
        var state = State(transfer, "hdmv_pgs_subtitle");
        state.BaseRequest.StartTimeTicks = startTicks;
        var helper = Helper();

        Assert.True(helper.UsesHdrPgsCache(state, Options()));
        var subtitleArguments = helper.GetHdrPgsInputArgument(state, Options());
        Assert.Equal(" -start_at_zero -isync 0 -canvas_size 1920x1080 -i file:\"/media/hdr-pgs.sup\"", subtitleArguments);
        Assert.DoesNotContain("-ss", subtitleArguments, StringComparison.Ordinal);
        var inputArguments = helper.GetInputArgument(state, Options(), "mp4");
        Assert.Contains(" -i file:\"/media/source.mkv\"" + subtitleArguments, inputArguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("smpte2084", "hdmv_pgs_subtitle", true, 2)]
    [InlineData("arib-std-b67", "hdmv_pgs_subtitle", true, 2)]
    [InlineData("smpte2084", "hdmv_pgs_subtitle", false, 1)]
    [InlineData("smpte2084", "ass", true, 1)]
    public void ExternalAudioMapping_FollowsAnyHdrPgsCacheInput(string transfer, string subtitle, bool enabled, int expectedAudioInput)
    {
        var state = State(transfer, subtitle);
        var audio = new MediaStream
        {
            Index = 2,
            Type = MediaStreamType.Audio,
            Codec = "aac",
            IsExternal = true,
            Path = "/media/external.mka"
        };
        state.AudioStream = audio;
        state.MediaSource.MediaStreams = [.. state.MediaSource.MediaStreams, audio];
        var options = Options();
        options.EnableAv1HdrTranscoding = enabled;
        var helper = Helper();

        var inputArguments = helper.GetInputArgument(state, options, "mp4");
        var mainIndex = inputArguments.IndexOf(" -i file:\"/media/source.mkv\"", StringComparison.Ordinal);
        var cacheIndex = inputArguments.IndexOf(" -i file:\"/media/hdr-pgs.sup\"", StringComparison.Ordinal);
        var audioIndex = inputArguments.IndexOf(" -i \"/media/external.mka\"", StringComparison.Ordinal);
        Assert.True(mainIndex >= 0 && audioIndex > mainIndex);
        if (expectedAudioInput == 2)
        {
            Assert.True(cacheIndex > mainIndex && audioIndex > cacheIndex);
        }
        else
        {
            Assert.Equal(-1, cacheIndex);
        }

        var mapArguments = helper.GetMapArgs(state, options);
        Assert.Contains($" -map {expectedAudioInput}:0", mapArguments, StringComparison.Ordinal);
        Assert.DoesNotContain($" -map {(expectedAudioInput == 2 ? 1 : 2)}:0", mapArguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sdr")]
    [InlineData("text")]
    [InlineData("no-subtitle")]
    [InlineData("external")]
    [InlineData("soft")]
    [InlineData("copy")]
    [InlineData("disabled")]
    [InlineData("other-bitmap")]
    public void NonApplicableSubtitlePaths_DoNotUseHdrPgsCache(string scenario)
    {
        var state = State(scenario == "sdr" ? "bt709" : "smpte2084", scenario == "no-subtitle" ? null : "hdmv_pgs_subtitle");
        var options = Options();
        switch (scenario)
        {
            case "text":
                state = State(subtitleCodec: "ass");
                break;
            case "external":
                state.SubtitleStream.IsExternal = true;
                break;
            case "soft":
                state.SubtitleDeliveryMethod = SubtitleDeliveryMethod.External;
                break;
            case "copy":
                state.OutputVideoCodec = "copy";
                break;
            case "disabled":
                options.EnableAv1HdrTranscoding = false;
                break;
            case "other-bitmap":
                state.SubtitleStream.Codec = "dvd_subtitle";
                break;
        }

        var helper = Helper(cacheFailure: true);
        Assert.False(helper.UsesHdrPgsCache(state, options));
        Assert.Empty(helper.GetHdrPgsInputArgument(state, options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void InternalHdrPgsBurnIn_EmptyCachePathIsRejected(string? cachePath)
    {
        var state = State(subtitleCodec: "hdmv_pgs_subtitle");
        var helper = Helper(pgsCachePath: cachePath);

        var error = Record.Exception(() => helper.GetInputArgument(state, Options(), "mp4"));
        Assert.IsAssignableFrom<ArgumentException>(error);
    }

    [Fact]
    public void InternalHdrPgsBurnIn_CacheFailureDoesNotFallBack()
    {
        var state = State(subtitleCodec: "hdmv_pgs_subtitle");

        Assert.Throws<InvalidOperationException>(() => Helper(cacheFailure: true).GetInputArgument(state, Options(), "mp4"));
    }

    [Theory]
    [InlineData(TranscodingJobType.Progressive, false, true)]
    [InlineData(TranscodingJobType.Progressive, true, false)]
    [InlineData(TranscodingJobType.Hls, false, false)]
    public void TextSubtitleSeek_PreservesTheMainClock(TranscodingJobType jobType, bool copyTimestamps, bool needsOffset)
    {
        var state = State(subtitleCodec: "ass");
        state.TranscodingType = jobType;
        state.BaseRequest.CopyTimestamps = copyTimestamps;
        state.BaseRequest.StartTimeTicks = 123456789;
        var graph = Helper().GetQsvAv1HdrFilters(state, Options(), "hevc_vaapi", "av1_qsv", VideoRangeType.HDR10);
        var subtitle = string.Join(',', graph.SubFilters);

        if (needsOffset)
        {
            Assert.Contains("[av1hdr_canvas]setparams=alpha_mode=straight,setpts=PTS+12.3456789/TB,subtitles=", subtitle, StringComparison.Ordinal);
            Assert.Contains(":alpha=1:sub2video=1:sub2video_skip=0,setpts=PTS-12.3456789/TB", subtitle, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("[av1hdr_canvas]setparams=alpha_mode=straight,subtitles=", subtitle, StringComparison.Ordinal);
            Assert.DoesNotContain("setpts=", subtitle, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OriginalTextSubtitleFilter_PreservesItsProgressiveSeekBehavior()
    {
        var state = State("bt709", "ass");
        state.TranscodingType = TranscodingJobType.Progressive;
        state.BaseRequest.StartTimeTicks = 123456789;

        var filter = Helper().GetTextSubtitlesFilter(state, true, true);

        Assert.Contains(",setpts=PTS -12/TB", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("setpts=PTS+12.3456789/TB", filter, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(null)]
    [InlineData("hdmv_pgs_subtitle")]
    [InlineData("srt")]
    [InlineData("ass")]
    public void SdrTopLevelFilterRoute_IsUnchanged(string? subtitle)
    {
        var helper = Helper();
        var state = State("bt709", subtitle);
        var options = Options();
        options.EnableAv1HdrTranscoding = false;
        var before = helper.GetVideoProcessingFilterParam(state, options, "av1_qsv");
        options.EnableAv1HdrTranscoding = true;
        var after = helper.GetVideoProcessingFilterParam(state, options, "av1_qsv");
        Assert.Equal(before, after);
    }

    [Fact]
    public void InvalidDecoder_DoesNotSilentlyProduceEightBitHdr()
    {
        Assert.Throws<NotSupportedException>(() => Helper().GetQsvAv1HdrFilters(State(), Options(), string.Empty, "av1_qsv", VideoRangeType.HDR10));
    }

    [Theory]
    [InlineData("smpte2084")]
    [InlineData("arib-std-b67")]
    public void HdrEncoderArguments_PreserveStaticColorAndRemoveStaleDynamicMetadata(string transfer)
    {
        var state = State(transfer);
        var args = Helper().GetVideoQualityParam(state, "av1_qsv", Options(), EncoderPreset.slow);
        Assert.Equal(15, Av1HdrTranscoding.GetOutputLevel(state));
        Assert.Contains(" -level 53", args, StringComparison.Ordinal);
        Assert.Contains("-color_trc:v:0 " + transfer, args, StringComparison.Ordinal);
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
                if (subtitle == "hdmv_pgs_subtitle")
                {
                    state.SubtitleStream.Width = 640;
                    state.SubtitleStream.Height = 360;
                }

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
                    SubtitleInputArguments = helper.GetHdrPgsInputArgument(state, Options()),
                    FilterArguments = helper.FormatVideoFilterGraph(state, graph.MainFilters, graph.SubFilters, graph.OverlayFilters, helper.UsesHdrPgsCache(state, Options())),
                    ColorArguments = helper.GetVideoQualityParam(state, "av1_qsv", Options(), EncoderPreset.slow)
                });
            }
        }

        File.WriteAllText(path, JsonSerializer.Serialize(fixtures));
    }

    [Fact]
    public void ExportPerformanceFixtures_WhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("JELLYFIN_HDR_PERF_FIXTURES");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var fixtures = new List<object>();
        foreach (var (width, height, bitrate) in new[] { (1920, 1080, 12000000), (3840, 2160, 30000000) })
        {
            foreach (var subtitle in new string?[] { null, "ass" })
            {
                var state = State(subtitleCodec: subtitle);
                state.BaseRequest.MaxWidth = width;
                state.BaseRequest.MaxHeight = height;
                state.OutputVideoBitrate = bitrate;
                var options = Options();
                var range = Av1HdrTranscoding.GetOutputRange(state, options)!.Value;
                var size = Av1HdrTranscoding.GetOutputSize(state);
                Assert.Equal(width, size.Width);
                Assert.Equal(height, size.Height);
                var helper = Helper();
                var graph = helper.GetQsvAv1HdrFilters(state, options, "hevc_vaapi", "av1_qsv", range);
                fixtures.Add(new
                {
                    Transfer = state.VideoStream.ColorTransfer,
                    Subtitle = subtitle,
                    SourceWidth = state.VideoStream.Width,
                    SourceHeight = state.VideoStream.Height,
                    SourceFramerate = state.VideoStream.RealFrameRate,
                    OutputWidth = size.Width,
                    OutputHeight = size.Height,
                    OutputVideoBitrate = bitrate,
                    Main = graph.MainFilters,
                    Sub = graph.SubFilters,
                    Overlay = graph.OverlayFilters,
                    SubtitleInputArguments = helper.GetHdrPgsInputArgument(state, options),
                    FilterArguments = helper.FormatVideoFilterGraph(state, graph.MainFilters, graph.SubFilters, graph.OverlayFilters, helper.UsesHdrPgsCache(state, options)),
                    ColorArguments = helper.GetVideoQualityParam(state, "av1_qsv", options, EncoderPreset.slow)
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
        HardwareDecodingCodecs = ["hevc", "vp9", "av1"],
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
                Path = subtitleCodec is "srt" or "ass" ? "/media/subtitles." + subtitleCodec : null,
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

    private static EncodingHelper Helper(bool libplacebo = true, bool vulkan = true, FilterOptionType? unsupportedOption = null, string? pgsCachePath = "/media/hdr-pgs.sup", bool cacheFailure = false)
    {
        var encoder = new Mock<IMediaEncoder>();
        encoder.SetupGet(x => x.EncoderVersion).Returns(new Version(8, 1, 3));
        encoder.Setup(x => x.SupportsFilter("libplacebo")).Returns(libplacebo);
        encoder.Setup(x => x.SupportsHwaccel("vulkan")).Returns(vulkan);
        encoder.Setup(x => x.GetInputPathArgument(It.IsAny<EncodingJobInfo>())).Returns("file:\"/media/source.mkv\"");
        encoder.Setup(x => x.SupportsFilterWithOption(It.IsAny<FilterOptionType>())).Returns((FilterOptionType option) =>
            option != unsupportedOption && option is FilterOptionType.LibplaceboRequireLinearBlend
                or FilterOptionType.LibplaceboHlgReferencePeak or FilterOptionType.SubtitlesSub2videoSkip);
        encoder.Setup(x => x.EscapeSubtitleFilterPath(It.IsAny<string>())).Returns((string x) => x);
        var subtitleEncoder = new Mock<ISubtitleEncoder>();
        var cache = subtitleEncoder.Setup(x => x.GetHdrPgsSubtitleFilePath(It.IsAny<MediaStream>(), It.IsAny<MediaSourceInfo>(), It.IsAny<CancellationToken>()));
        if (cacheFailure)
        {
            cache.ThrowsAsync(new InvalidOperationException("PGS cache extraction failed"));
        }
        else
        {
            cache.ReturnsAsync(pgsCachePath!);
        }

        return new EncodingHelper(
            Mock.Of<IApplicationPaths>(),
            encoder.Object,
            subtitleEncoder.Object,
            Mock.Of<IConfiguration>(),
            Mock.Of<IConfigurationManager>(),
            Mock.Of<IPathManager>());
    }
}
