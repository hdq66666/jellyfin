using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// HDR output policy for this fork's Wildcat Lake VAAPI/QSV pipeline.
/// </summary>
public static class Av1HdrTranscoding
{
    /// <summary>AV1 level 5.3, expressed as the sequence header level index.</summary>
    public const int OutputLevel = 15;

    /// <summary>Gets whether the dedicated hardware mode is enabled.</summary>
    /// <param name="options">The encoding configuration.</param>
    /// <returns>Whether all enabling switches are set.</returns>
    public static bool IsEnabled(EncodingOptions options)
        => options.EnableAv1HdrTranscoding
            && options.HardwareAccelerationType == HardwareAccelerationType.qsv
            && options.EnableHardwareEncoding
            && options.AllowAv1Encoding;

    /// <summary>Gets the fixed level shared by the encoder and HLS declaration.</summary>
    /// <param name="state">The encoding job.</param>
    /// <returns>The AV1 sequence header level index.</returns>
    public static int GetOutputLevel(EncodingJobInfo state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return OutputLevel;
    }

    /// <summary>Gets the dimensions produced by the hardware scale and transpose filters.</summary>
    /// <param name="state">The encoding job.</param>
    /// <returns>The output dimensions, or null dimensions when they cannot be determined.</returns>
    public static (int? Width, int? Height) GetOutputSize(EncodingJobInfo state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var video = state.VideoStream;
        var request = state.BaseRequest;
        if (video is null || request is null
            || video.Width is <= 0 || video.Height is <= 0
            || request.Width is <= 0 || request.Height is <= 0
            || request.MaxWidth is <= 0 || request.MaxHeight is <= 0)
        {
            return (null, null);
        }

        var swapDimensions = video.Rotation is 90 or -90;
        var (width, height) = EncodingHelper.GetFixedOutputSize(
            swapDimensions ? video.Height : video.Width,
            swapDimensions ? video.Width : video.Height,
            request.Width,
            request.Height,
            request.MaxWidth,
            request.MaxHeight);
        return width is > 0 && height is > 0 ? (width, height) : (null, null);
    }

    /// <summary>Checks a source without throwing or changing a streaming job.</summary>
    /// <param name="stream">The source video stream.</param>
    /// <returns>The compatible static HDR range, or null.</returns>
    public static VideoRangeType? GetSourceRange(MediaStream? stream)
    {
        if (GetSourceError(stream) is not null)
        {
            return null;
        }

        return string.Equals(stream!.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase)
            ? VideoRangeType.HLG
            : VideoRangeType.HDR10;
    }

    /// <summary>Gets whether this is an explicit HDR AV1 transcode in the dedicated mode.</summary>
    /// <param name="state">The encoding job.</param>
    /// <param name="options">The encoding configuration.</param>
    /// <returns>Whether HDR validation applies to this request.</returns>
    public static bool IsRequested(EncodingJobInfo state, EncodingOptions options)
        => IsEnabled(options)
            && state.BaseRequest is not null
            && !state.BaseRequest.Static
            && !EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
            && string.Equals(state.ActualOutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase)
            && state.VideoStream?.VideoRange == VideoRange.HDR;

    /// <summary>
    /// Gets the HDR output range, or null for the original pipeline.
    /// </summary>
    /// <param name="state">The encoding job.</param>
    /// <param name="options">The encoding configuration.</param>
    /// <returns>The output HDR range, when enabled for this job.</returns>
    public static VideoRangeType? GetOutputRange(EncodingJobInfo state, EncodingOptions options)
    {
        if (!IsRequested(state, options) || GetValidationError(state, options) is not null)
        {
            return null;
        }

        return GetSourceRange(state.VideoStream);
    }

    /// <summary>Rejects an unsupported explicit transcode before FFmpeg starts.</summary>
    /// <param name="state">The encoding job.</param>
    /// <param name="options">The encoding configuration.</param>
    public static void ValidateTranscode(EncodingJobInfo state, EncodingOptions options)
    {
        if (IsRequested(state, options) && GetValidationError(state, options) is { } reason)
        {
            throw new ArgumentException(reason, nameof(state));
        }
    }

    private static string? GetSourceError(MediaStream? stream)
    {
        if (stream is null || stream.VideoRange != VideoRange.HDR)
        {
            return "The source is not HDR.";
        }

        if (stream.BitDepth != 10
            || stream.PixelFormat is not ("yuv420p10le" or "p010le" or "p010"))
        {
            return "AV1 HDR requires a known 10-bit 4:2:0 source.";
        }

        if (!string.Equals(stream.ColorPrimaries, "bt2020", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(stream.ColorSpace, "bt2020nc", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(stream.ColorRange, "tv", StringComparison.OrdinalIgnoreCase)
            || !(string.Equals(stream.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stream.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase)))
        {
            return "AV1 HDR requires explicit limited-range BT.2020 non-constant-luminance PQ or HLG tags.";
        }

        var compatible = stream.VideoRangeType switch
        {
            VideoRangeType.HDR10 or VideoRangeType.HDR10Plus or VideoRangeType.HLG
                or VideoRangeType.DOVIWithHDR10 or VideoRangeType.DOVIWithEL
                or VideoRangeType.DOVIWithHDR10Plus or VideoRangeType.DOVIWithELHDR10Plus
                or VideoRangeType.DOVIWithHLG => true,
            VideoRangeType.DOVIInvalid => stream.BlPresentFlag == 1
                && stream.DvProfile != 5
                && ((stream.DvBlSignalCompatibilityId == 1 && string.Equals(stream.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase))
                    || (stream.DvBlSignalCompatibilityId == 4 && string.Equals(stream.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase))),
            _ => false
        };
        return compatible ? null : "The source has no verified HDR10 or HLG compatible base layer.";
    }

    private static string? GetValidationError(EncodingJobInfo state, EncodingOptions options)
    {
        if (GetSourceError(state.VideoStream) is { } sourceError)
        {
            return sourceError;
        }

        if (!options.PreferSystemNativeHwDecoder)
        {
            return "AV1 HDR requires the native VAAPI decoder.";
        }

        if (options.HardwareDecodingCodecs is null
            || !options.HardwareDecodingCodecs.Contains(state.VideoStream.Codec, StringComparer.OrdinalIgnoreCase)
            || (string.Equals(state.VideoStream.Codec, "hevc", StringComparison.OrdinalIgnoreCase) && !options.EnableDecodingColorDepth10Hevc)
            || (string.Equals(state.VideoStream.Codec, "vp9", StringComparison.OrdinalIgnoreCase) && !options.EnableDecodingColorDepth10Vp9))
        {
            return "Enable native 10-bit hardware decoding for the source codec before requesting AV1 HDR.";
        }

        var profiles = state.GetRequestedProfiles("av1");
        if (profiles.Length != 0 && !profiles.Contains("main", StringComparer.OrdinalIgnoreCase)
            && !profiles.Contains("0", StringComparer.OrdinalIgnoreCase))
        {
            return "AV1 HDR requires the Main profile.";
        }

        var requestedLevel = state.GetRequestedLevel("av1");
        if (!string.IsNullOrEmpty(requestedLevel)
            && (!int.TryParse(requestedLevel, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
                || level < OutputLevel || level > 31))
        {
            return "The client must support AV1 level index 15 (5.3) or higher.";
        }

        var (width, height) = GetOutputSize(state);
        var rate = GetOutputFrameRate(state, options);

        // These bounds are necessary but the encoder must also satisfy the AV1 buffer and tile limits.
        if (!width.HasValue || !height.HasValue || width <= 0 || height <= 0
            || width > 8192 || height > 4352 || (long)width * height > 8912896
            || !rate.HasValue || !float.IsFinite(rate.Value) || rate <= 0
            || (double)width * height * rate > 1069547520
            || state.OutputVideoBitrate is null or <= 0 or > 60000000)
        {
            return "The requested output exceeds the validated AV1 Main-tier level 5.3 envelope.";
        }

        return null;
    }

    /// <summary>Gets the output frame rate used to validate and describe the HDR stream.</summary>
    /// <param name="state">The encoding job.</param>
    /// <param name="options">The encoding configuration.</param>
    /// <returns>The effective frame rate or conservative source rate, or null when unknown.</returns>
    public static float? GetOutputFrameRate(EncodingJobInfo state, EncodingOptions options)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);

        var request = state.BaseRequest;
        var video = state.VideoStream;
        if (request is null || video is null)
        {
            return null;
        }

        if (request.Framerate.HasValue)
        {
            return float.IsFinite(request.Framerate.Value) && request.Framerate > 0
                ? request.Framerate
                : null;
        }

        var maxRate = request.MaxFramerate;
        if (maxRate.HasValue && (!float.IsFinite(maxRate.Value) || maxRate <= 0))
        {
            return null;
        }

        // GetFramerateParam only emits a max-rate filter when the reference rate exceeds it.
        var referenceRate = video.ReferenceFrameRate;
        if (maxRate.HasValue && referenceRate > maxRate)
        {
            return maxRate;
        }

        var rate = video.RealFrameRate ?? video.AverageFrameRate;
        if (!rate.HasValue || !float.IsFinite(rate.Value) || rate <= 0)
        {
            return null;
        }

        if (video.AverageFrameRate is > 0 and < 1000 and var average && float.IsFinite(average))
        {
            rate = Math.Max(rate.Value, average);
        }

        if (video.IsInterlaced && options.DeinterlaceDoubleRate && referenceRate <= 30)
        {
            rate *= 2;
        }

        return float.IsFinite(rate.Value) ? rate : null;
    }

    /// <summary>
    /// Gets the FFmpeg transfer characteristic for an HDR range.
    /// </summary>
    /// <param name="range">The output range.</param>
    /// <returns>The FFmpeg transfer name.</returns>
    public static string GetTransfer(VideoRangeType range)
        => range == VideoRangeType.HLG ? "arib-std-b67" : "smpte2084";

    /// <summary>
    /// Gets the linear-light HDR subtitle compositor.
    /// </summary>
    /// <param name="range">The output range.</param>
    /// <returns>The libplacebo composition filter.</returns>
    public static string GetSubtitleCompositionFilter(VideoRangeType range)
        => "libplacebo=inputs=2:format=p010le:colorspace=bt2020nc:color_primaries=bt2020"
            + $":color_trc={GetTransfer(range)}:range=tv:peak_detect=0:apply_dolbyvision=0:apply_filmgrain=0"
            + ":tonemapping=clip:gamut_mode=clip:contrast_recovery=0:dithering=none:require_linear_blend=1"
            + (range == VideoRangeType.HLG ? ":hlg_reference_peak=1000" : string.Empty);
}
