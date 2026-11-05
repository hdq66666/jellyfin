# AV1 HDR on Wildcat Lake

This fork targets one machine class: Intel Core 3 304 / Wildcat Lake on Linux,
using the `xe` kernel driver, native VAAPI decoding and scaling, and the QSV AV1
encoder. It is not a general HDR capability-detection implementation.

## Behavior

`EnableAv1HdrTranscoding` defaults to `true` in this fork. Select Intel QSV,
enable the appropriate hardware decoders and AV1 encoding, and retain
`PreferSystemNativeHwDecoder=true`. HEVC output encoding is not required.

For a **HDR source being transcoded to AV1**, the new path:

- Preserves 10-bit pixels through P010 scaling and AV1 encoding.
- Preserves PQ or HLG and BT.2020, with limited-range output. It does not insert
  tone mapping or the VPP brightness adjustment.
- Declares `VIDEO-RANGE=PQ` or `HLG` and 10-bit AV1 in the HLS master playlist.
- Carries an HDR10-compatible Dolby Vision base layer as HDR10. It does not
  manufacture a Dolby Vision RPU or HDR10+ dynamic metadata for the new video.
  Dolby Vision without a compatible base layer is rejected rather than
  interpreted as PQ.

**SDR sources retain the original filter chain and encoder arguments**, including
their existing subtitle path. Copy/remux retains the original video bitstream;
the legacy H.264 SDR fallback playlist is omitted while this HDR mode is enabled.
Setting `EnableAv1HdrTranscoding=false` restores the original AV1 transcode path.

The fork assumes an HDR-capable playback environment instead of selecting SDR
based on a client's advertised range. The target display and player must be able
to present AV1 HDR. This does not turn an SDR display into an HDR display.

## Subtitle composition

The target's `overlay_qsv` rejects P010 composition. With burned-in subtitles,
the main picture is downloaded **after hardware scaling**, remains 10-bit, and
uses software `overlay` before being uploaded for AV1 hardware encoding. Without
burn-in, the picture stays on the GPU.

PGS-style bitmap subtitles use straight alpha. SRT/ASS rendered through libass
use premultiplied RGB. FFmpeg's alpha mode negotiation converts them to straight
alpha before color conversion. Alpha is then split from RGB, the RGB plane is
converted to the output HDR transfer with 203-nit reference white, and alpha is
merged back. Keeping alpha outside the nonlinear conversion prevents translucent
white glyphs from being boosted above reference white.

The overlay is `yuv420p10` with straight alpha, `eof_action=pass` and
`repeatlast=0`. Subtitle white is not interpreted as 10,000-nit PQ white. This
requires the alpha mode support in the tested Jellyfin FFmpeg 8.1.3 build.

## Validation

Target userspace: Jellyfin FFmpeg 8.1.3, Intel iHD 26.3.5, libva 2.24.1.

Unit tests cover HDR range selection, unsupported Dolby Vision, 10-bit filter
chains, PGS/SRT/ASS composition, HLS range/codec declarations, and unchanged SDR
graphs and encoder arguments.

Export the actual C#-generated graphs, then validate them on the target hardware:

```bash
JELLYFIN_HDR_FIXTURES=/tmp/av1-hdr-fixtures.json \
  dotnet test tests/Jellyfin.Controller.Tests/Jellyfin.Controller.Tests.csproj \
  -c Release --filter FullyQualifiedName~Av1HdrTranscodingTests

python3 tests/hardware/validate_av1_hdr.py --fixtures /tmp/av1-hdr-fixtures.json
```

The hardware script uses synthetic inputs, not a private film or credentials.
It uses a 1280×720 synthetic source scaled to 640×360 and verifies 10-bit AV1,
output dimensions, stream and decoded-frame color tags, and frame count for PQ/HLG
with no subtitles, bitmap subtitles, SRT and ASS. It also measures transparent
and premultiplied subtitle pixels: limited-range black is code 64; 203-nit PQ
white is approximately code 573; half opacity retains its alpha independently
of white luminance. Temporary samples and caches are removed automatically.

The bitmap test exercises the decoded subtitle plane; it is not a test of a PGS
demuxer. Short synthetic encoding tests do not establish long-running playback
stability, performance for every resolution, or correct rendering on every HDR
display. Software subtitle composition has a CPU and memory-bandwidth cost.

## Upstream context

- [Server PR #18504](https://github.com/jellyfin/jellyfin/pull/18504) discusses
  HDR-preserving transcodes and HLS signaling, but targets HEVC Main10 and is not
  merged. Its author did not validate QSV.
- [FFmpeg PR #733](https://github.com/jellyfin/jellyfin-ffmpeg/pull/733) is the
  broader hardware capability-probing effort referenced by the maintainers.

This fork deliberately implements and tests a narrower VAAPI/QSV AV1 path.
Neither simply installing the current official Unstable build nor disabling
tone mapping alone provides this behavior.
