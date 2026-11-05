# FFmpeg changes required by this fork

Base: Jellyfin FFmpeg `v8.1.3-2`, with the **complete official Debian patch
series already applied**, and libplacebo API 360 (the official build pins
`cee9b076f2c63104ccfd497fa79c39a867293ec4`). Apply these patches in order:

1. `0001-libplacebo-linear-hdr-composition.patch`
2. `0002-subtitles-keep-empty-frames.patch`

For example, from the prepared FFmpeg source directory:

```sh
git apply --check /path/to/jellyfin/patches/ffmpeg/0001-libplacebo-linear-hdr-composition.patch
git apply /path/to/jellyfin/patches/ffmpeg/0001-libplacebo-linear-hdr-composition.patch
git apply /path/to/jellyfin/patches/ffmpeg/0002-subtitles-keep-empty-frames.patch
```

Build with the official Jellyfin packaging configuration and preserve its
existing codecs, drivers and dependencies. A minimal validation build is not a
production replacement. Check actual VAAPI DRM initialization under the service
user; a listed `vaapi` hwaccel alone does not establish this capability.

All new options preserve upstream behavior by default. The server enables
`require_linear_blend=1` for HDR subtitle composition, `hlg_reference_peak=1000`
for HLG, and `sub2video_skip=0` for text subtitles. Unsupported options are
detected by the server before burn-in is started.

The linear-blend option rejects incompatible FBO settings, texture allocation
failures and reported renderer failures. The HLG option fills only missing
internal luminance metadata and does not create frame mastering metadata.
The subtitle option preserves the existing straight-alpha path while retaining
empty canvas frames, preventing sparse subtitles from starving the compositor.

Run the C# fixture exporters and `tests/hardware/validate_av1_hdr.py` against the
resulting binary before replacing a running installation. The test must use the
same GPU and service-user permissions as Jellyfin.

Embedded PGS is extracted to a separate, atomic `.hdr-pgs.sup` cache with
`-copyts`; it is read from the beginning as a second input using `-isync 0`.
The HDR video and subtitle clock both use AVTB before splitting. Cache
extraction failures are reported without using a shared video/subtitle clock.
