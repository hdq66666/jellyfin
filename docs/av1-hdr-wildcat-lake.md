# AV1 HDR on Wildcat Lake

This fork targets Intel Core 3 304 / Wildcat Lake on Linux with the `xe` driver,
native VAAPI decoding/scaling and QSV AV1 encoding. It is not a general hardware
capability selection system.

## Output contract

`EnableAv1HdrTranscoding` defaults to `true`. Enable Intel QSV, AV1 hardware
encoding, native hardware decoding and the source codec's 10-bit decoder.

An eligible HDR-to-AV1 request produces Main-profile, 10-bit, limited-range
BT.2020 AV1 with the source's PQ or HLG transfer. It never requests HDR-to-SDR
tone mapping or VPP brightness adjustment. Source bit depth, chroma format,
range, matrix, primaries and compatible HDR base layer must be known. An
unsupported explicit HDR transcode fails; an unavailable optional HLS transcode
variant is omitted without breaking original copy/remux playback.

The encoder and HLS declaration use AV1 level index 15 (level 5.3), Main tier.
Output size, frame rate, bitrate and buffer limits are checked. The HLS master
uses the complete AV1 color string, matching VIDEO-RANGE, actual rotated/scaled
resolution and output frame rate. Requested client dynamic range does not
convert an HDR source to SDR. Playback requires an AV1 HDR display path.

Compatible Dolby Vision base layers are carried as static HDR. Dolby Vision
RPU and HDR10+ metadata are removed from the new bitstream. This does not
reconstruct dynamic metadata for the transcoded picture.

SDR sources retain the original filter and encoder paths. Disabling the new
option restores the original AV1 path. The no-subtitle HDR path stays on VAAPI
and QSV and does not require the subtitle compositor.

## Subtitle composition

PGS, SRT and ASS burn-in uses a GPU linear-light compositor:

```text
VAAPI decode -> deinterlace/rotate/fps/scale P010 -> split
  main:   DRM PRIME -> libplacebo -> Vulkan -> VAAPI -> QSV AV1
  clock:  16x16 download -> transparent BGRA canvas -> subtitles --^
```

The canvas is derived from each main-video frame, after any frame-rate change.
It shares the main PTS and EOF; it is not an independent infinite video source.
Only the tiny clock image is downloaded. The main picture is not converted to
8-bit or composited in PQ/HLG code values.

Jellyfin FFmpeg's packed BGRA `sub2video` rendering outputs straight alpha.
Text rendering keeps empty frames and adjusts progressive seek time before
rendering, then restores the main clock.

Embedded PGS is extracted once to an independent `.hdr-pgs.sup` cache with
absolute timestamps. The second input is not fast-seeked, so its palette,
epoch and any active cue are available after a video seek. `-isync 0` aligns
source starts and progressive seek offsets; HLS uses `-copyts -start_at_zero`.
The first extraction reads the source sequentially; subsequent playback reads
only the small subtitle cache. Existing ordinary subtitle caches and SDR
extraction are unchanged. Extraction errors are reported without falling back
to the shared video/subtitle input. Bitmap subtitles are composed onto the
same finite transparent canvas. Both branches use AVTB before splitting, so
a heartbeat just before a clear event cannot round onto the clear timestamp.

Subtitle color is explicitly SDR BT.709 with
203-nit reference white. Libplacebo converts both inputs to a linear floating
point intermediate and then back to the original HDR transfer.

## Required FFmpeg patches

Official Jellyfin FFmpeg 8.1.3-2 contains the relevant Vulkan, DRM PRIME and
libplacebo functionality, but does not provide all required behavior. This fork
requires additional patches for HDR subtitle burn-in:

- `require_linear_blend=1`: fail if the linear composition texture or renderer
  fails, instead of silently blending nonlinear HDR values.
- `hlg_reference_peak=1000`: fill missing internal HLG display luminance metadata
  with a 1000-nit reference and near-zero black. This prevents the intermediate
  linear color space from introducing an SDR black point. Existing nonzero
  luminance metadata is retained; no mastering side data is fabricated.
- `sub2video_skip=0`: retain transparent text-canvas frames during subtitle gaps
  while preserving the patched straight-alpha renderer.

The server probes these filter options and refuses unsupported burn-in before
starting FFmpeg. Merely installing the official nightly server or disabling
its tone-mapping checkbox is insufficient. Apply the fork patches after the
complete official Jellyfin FFmpeg patch series; raw upstream FFmpeg source is
not equivalent to the packaged executable.

The isolated test build is for validation. A replacement production package
must retain the official build's codecs and dependencies so the existing SDR
paths remain available.

## Validation

Controller and API tests cover policy, copy/remux preservation, HLS variants,
full AV1 color strings, rotated output geometry, frame rate, progressive seek,
subtitle graphs and unchanged SDR behavior. Hardware fixtures export the actual
production graph formatter and HLS codec strings.

`tests/hardware/validate_av1_hdr.py` validates the exported parameters on the
target GPU. Synthetic HEVC sources and original ASS/SRT/PGS fixtures avoid
private media. Checks include full-plane linear-light reference pixels, AV1
sequence headers, container/frame metadata, subtitle gaps and natural EOF.
Export fixtures with `JELLYFIN_HDR_FIXTURES` while running
`Av1HdrTranscodingTests.ExportHardwareFixtures_WhenRequested`, and
`JELLYFIN_HDR_HLS_FIXTURES` while running
`DynamicHlsVariantTests.ExportHdrHlsFixtures_WhenRequested`. Then run:

```sh
python3 tests/hardware/validate_av1_hdr.py \
  --fixtures /path/to/filters.json --hls-fixtures /path/to/hls.json \
  --ffmpeg /path/to/patched/ffmpeg --work-dir /path/to/temporary-work
```

Samples are temporary and removed after each run. Performance and long-gap
checks are separate from unit tests; a successful build alone is not evidence
of correct pixels or real-time playback.

## Upstream references

- [Server #18504](https://github.com/jellyfin/jellyfin/pull/18504): open HDR
  transcode proposal focused on HEVC Main10, not a ready AV1/QSV solution.
- [FFmpeg #733](https://github.com/jellyfin/jellyfin-ffmpeg/pull/733): broader
  hardware capability probing work.
- [AV1 CODECS specification](https://aomediacodec.github.io/av1-isobmff/#codecsparam):
  the declared profile, level, depth and color fields must match the bitstream.

## Measured limits

An eight-second moving 4K24 HEVC synthetic input, using the exported QSV
`veryfast` arguments, produced the following results including initialization:

| Output | No burned subtitles | ASS burn-in |
| --- | ---: | ---: |
| 1080p, 12 Mbps | 170 fps | 53 fps |
| 4K, 30 Mbps | 102 fps | 17 fps |

These short synthetic results do not establish sustained movie throughput.
The current 4K subtitle compositor did not reach real-time 24 fps. No automatic
resolution reduction is applied. The 90-second sparse-text test completed with
2160 frames and approximately 108 MiB peak process RSS.

The dedicated PGS cache currently supports local files. The cache container's
32-bit 90 kHz timestamps and FFmpeg's requirement for known input start times
remain format limits; extreme absolute timestamps and missing start times are
not covered by the target validation. Cache extraction from ISO uses the
configured FFmpeg input wrapper and must retain its existing disc support.

A real UHD ISO with a 600-second source start time was also checked: PGS
extraction retained absolute timestamps, and a 175-frame sample produced
175 AV1 Main 10-bit PQ frames with normalized PTS error below 1 millisecond.
