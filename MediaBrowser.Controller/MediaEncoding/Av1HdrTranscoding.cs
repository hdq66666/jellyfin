using System;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// HDR output policy for this fork's Wildcat Lake VAAPI/QSV pipeline.
/// </summary>
public static class Av1HdrTranscoding
{
    /// <summary>
    /// Gets the HDR output range, or null for the original pipeline.
    /// </summary>
    /// <param name="state">The encoding job.</param>
    /// <param name="options">The encoding configuration.</param>
    /// <returns>The output HDR range, when enabled for this job.</returns>
    public static VideoRangeType? GetOutputRange(EncodingJobInfo state, EncodingOptions options)
    {
        if (!options.EnableAv1HdrTranscoding
            || options.HardwareAccelerationType != HardwareAccelerationType.qsv
            || !options.EnableHardwareEncoding
            || state.BaseRequest.Static
            || EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
            || !string.Equals(state.ActualOutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase)
            || state.VideoStream?.VideoRange != VideoRange.HDR)
        {
            return null;
        }

        if (EncodingHelper.GetVideoColorBitDepth(state) < 10)
        {
            throw new NotSupportedException("AV1 HDR requires a source with at least 10-bit precision.");
        }

        // Preserve a compatible static base layer; never reinterpret profile 5 IPT as PQ.
        return state.VideoStream.VideoRangeType switch
        {
            VideoRangeType.HDR10 or VideoRangeType.HDR10Plus
                or VideoRangeType.DOVIWithHDR10 or VideoRangeType.DOVIWithEL
                or VideoRangeType.DOVIWithHDR10Plus or VideoRangeType.DOVIWithELHDR10Plus => VideoRangeType.HDR10,
            VideoRangeType.HLG or VideoRangeType.DOVIWithHLG => VideoRangeType.HLG,
            _ => throw new NotSupportedException("AV1 HDR requires an HDR10 or HLG compatible base layer.")
        };
    }

    /// <summary>
    /// Gets the FFmpeg transfer characteristic for an HDR range.
    /// </summary>
    /// <param name="range">The output range.</param>
    /// <returns>The FFmpeg transfer name.</returns>
    public static string GetTransfer(VideoRangeType range)
        => range == VideoRangeType.HLG ? "arib-std-b67" : "smpte2084";

    /// <summary>
    /// Converts an SDR subtitle plane to HDR with 203-nit reference white, retaining alpha.
    /// </summary>
    /// <param name="range">The output range.</param>
    /// <param name="premultiplied">Whether the subtitle renderer supplied premultiplied RGB.</param>
    /// <returns>The subtitle color conversion filter chain.</returns>
    public static string GetSubtitleColorFilter(VideoRangeType range, bool premultiplied = false)
        => (premultiplied ? "setparams=alpha_mode=premultiplied," : string.Empty)
            + "format=pix_fmts=gbrap16le:alpha_modes=straight,"
            // Keep alpha out of zscale's nonlinear color conversion: otherwise partially
            // transparent white can exceed the reference white even before composition.
            + "split=2[av1hdr_rgb][av1hdr_alpha];[av1hdr_alpha]alphaextract[av1hdr_a];"
            + "[av1hdr_rgb]format=gbrp16le,zscale=pin=bt709:tin=bt709:min=gbr:rin=full"
            + $":p=bt2020:t={GetTransfer(range)}:m=bt2020nc:r=limited:npl=203,format=yuv420p10le[av1hdr_c];"
            + "[av1hdr_c][av1hdr_a]alphamerge,format=pix_fmts=yuva420p10le:alpha_modes=straight";
}
