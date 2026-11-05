#!/usr/bin/env python3
"""Validate exported C# filter graphs on the target Linux VAAPI/QSV hardware.

Generate fixtures using Av1HdrTranscodingTests with JELLYFIN_HDR_FIXTURES set.
This script uses synthetic inputs and removes all temporary sample files.
"""
import argparse
import json
import os
from pathlib import Path
import shlex
import struct
import subprocess
import sys
import tempfile


def run(command, data=None):
    result = subprocess.run(command, input=data, capture_output=True, timeout=90)
    if result.returncode:
        raise RuntimeError(result.stderr.decode(errors="replace")[-6000:])
    return result


def pq_code(nits):
    m1, m2 = 2610 / 16384, 2523 / 32
    c1, c2, c3 = 3424 / 4096, 2413 / 128, 2392 / 128
    luminance = (nits / 10000) ** m1
    return round(64 + 876 * ((c1 + c2 * luminance) / (1 + c3 * luminance)) ** m2)


def validate_reference_white(ffmpeg, fixtures):
    # Check both bitmap straight alpha and libass-style premultiplied RGB.
    for codec, pixel in (("hdmv_pgs_subtitle", (255, 255, 255, 128)), ("srt", (128, 128, 128, 128))):
        fixture = next(f for f in fixtures if f["Subtitle"] == codec and f["Transfer"] == "smpte2084")
        color_filter = fixture["Sub"][-1]
        cmd = [ffmpeg, "-hide_banner", "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba",
               "-video_size", "4x4", "-i", "pipe:0", "-frames:v", "1", "-vf", color_filter,
               "-pix_fmt", "yuva420p10le", "-f", "rawvideo", "pipe:1"]
        data = run(cmd, bytes(pixel) * 16).stdout
        y = struct.unpack_from("<H", data)[0]
        alpha = struct.unpack_from("<H", data, 48)[0]
        assert abs(y - pq_code(203)) <= 3, (codec, "white", y, pq_code(203))
        assert abs(alpha - round(128 / 255 * 1023)) <= 3, (codec, "alpha", alpha)
        black = run(cmd, bytes((0, 0, 0, 0)) * 16).stdout
        assert struct.unpack_from("<H", black)[0] == 64, (codec, "black")
        assert struct.unpack_from("<H", black, 48)[0] == 0, (codec, "transparent")
        print(json.dumps({"test": "subtitle_reference_white", "subtitle": codec, "luma": y,
                          "expected_luma": pq_code(203), "alpha": alpha}), flush=True)


def validate_graph(ffmpeg, ffprobe, device, fixture, directory):
    transfer = fixture["Transfer"]
    subtitle = fixture["Subtitle"]
    output = directory / (transfer + "-" + str(subtitle) + ".mp4")
    command = [ffmpeg, "-hide_banner", "-nostdin", "-v", "warning",
               "-init_hw_device", "vaapi=va:" + device, "-init_hw_device", "qsv=qs@va",
               "-filter_hw_device", "qs", "-f", "lavfi", "-i",
               "testsrc2=size=1280x720:rate=24,format=p010le,"
               "setparams=color_primaries=bt2020:color_trc=" + transfer + ":colorspace=bt2020nc:range=limited"]
    main = "hwupload=derive_device=vaapi:extra_hw_frames=24," + ",".join(fixture["Main"])
    if subtitle is None:
        graph = "[0:v]" + main + "[out]"
    else:
        sub = ",".join(fixture["Sub"])
        if subtitle == "hdmv_pgs_subtitle":
            command += ["-f", "lavfi", "-i", "color=c=white@0.5:size=1920x1080:rate=24,format=bgra"]
            sub = "[1:v]" + sub
        else:
            path = directory / ("subtitles." + subtitle)
            if subtitle == "srt":
                path.write_text("1\n00:00:00,000 --> 00:00:10,000\nHDR subtitle test\n")
            else:
                path.write_text("[Script Info]\nScriptType: v4.00+\nPlayResX: 1280\nPlayResY: 720\n"
                                "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, Alignment\n"
                                "Style: Default,DejaVu Sans,42,&H00FFFFFF,2\n"
                                "[Events]\nFormat: Layer, Start, End, Style, Text\n"
                                "Dialogue: 0,0:00:00.00,0:00:10.00,Default,HDR subtitle test\n")
            sub = sub.replace("/media/subtitles." + subtitle, str(path))
        graph = "[0:v]" + main + "[main];" + sub + "[sub];[main][sub]" + ",".join(fixture["Overlay"]) + "[out]"
    command += ["-filter_complex", graph, "-map", "[out]", "-an", "-frames:v", "48", "-c:v", "av1_qsv"]
    command += shlex.split(fixture["ColorArguments"])
    command += ["-y", str(output)]
    result = run(command)
    probe = json.loads(run([ffprobe, "-v", "error", "-select_streams", "v:0", "-show_streams",
                            "-show_frames", "-count_frames", "-of", "json", str(output)]).stdout)
    stream = probe["streams"][0]
    expected = {"codec_name": "av1", "width": 640, "height": 360, "pix_fmt": "yuv420p10le", "color_range": "tv",
                "color_primaries": "bt2020", "color_space": "bt2020nc", "color_transfer": transfer}
    for key, value in expected.items():
        assert stream.get(key) == value, (subtitle, key, stream.get(key), value)
    assert int(stream["nb_read_frames"]) == 48, stream
    frames = probe.get("frames", [])
    assert len(frames) == 48, len(frames)
    for frame in frames:
        for key in ("pix_fmt", "color_range", "color_primaries", "color_space", "color_transfer"):
            assert frame.get(key) == expected[key], (subtitle, "decoded frame", key, frame.get(key))
    print(json.dumps({"test": "encoded_graph", "subtitle": subtitle, "transfer": transfer,
                      "frames": 48, "bytes": output.stat().st_size,
                      "warnings": result.stderr.decode(errors="replace")[-1500:]}), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixtures", default="-", help="Exported JSON, or - for stdin")
    parser.add_argument("--ffmpeg", default="/usr/lib/jellyfin-ffmpeg/ffmpeg")
    parser.add_argument("--ffprobe", default="/usr/lib/jellyfin-ffmpeg/ffprobe")
    parser.add_argument("--device", default="/dev/dri/renderD128")
    args = parser.parse_args()
    fixtures = json.load(sys.stdin) if args.fixtures == "-" else json.loads(Path(args.fixtures).read_text())
    validate_reference_white(args.ffmpeg, fixtures)
    with tempfile.TemporaryDirectory(prefix="jellyfin-av1-hdr-validation-") as directory:
        os.environ["XDG_CACHE_HOME"] = directory
        os.environ["NEO_CACHE_DIR"] = directory
        for fixture in fixtures:
            validate_graph(args.ffmpeg, args.ffprobe, args.device, fixture, Path(directory))


if __name__ == "__main__":
    main()
