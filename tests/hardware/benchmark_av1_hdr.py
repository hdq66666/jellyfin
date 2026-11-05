#!/usr/bin/env python3
"""Measure the C#-exported 1080p/4K graphs on a finite moving HEVC test source."""
import argparse
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import tempfile
import time

sys.dont_write_bytecode = True
from subtitle_fixtures import write_subtitles  # noqa: E402


def run(command, timeout=180):
    result = subprocess.run(command, capture_output=True, timeout=timeout, check=False)
    if result.returncode:
        raise RuntimeError(result.stderr.decode(errors="replace")[-8000:])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixtures", required=True)
    parser.add_argument("--ffmpeg", required=True)
    parser.add_argument("--reference-ffmpeg", default="/usr/lib/jellyfin-ffmpeg/ffmpeg")
    parser.add_argument("--device", default="/dev/dri/renderD128")
    parser.add_argument("--work-dir", type=Path)
    parser.add_argument("--seconds", type=int, default=8)
    args = parser.parse_args()
    fixtures = json.loads(Path(args.fixtures).read_text())
    results = []
    with tempfile.TemporaryDirectory(prefix="hdr-benchmark-", dir=args.work_dir) as temp:
        directory = Path(temp)
        os.environ["XDG_CACHE_HOME"] = temp
        os.environ["NEO_CACHE_DIR"] = temp
        write_subtitles(directory)
        source = directory / "moving-4k-pq.mkv"
        hardware = ["-init_hw_device", "vaapi=va:" + args.device, "-init_hw_device", "qsv=qs@va",
                    "-filter_hw_device", "qs"]
        run([args.reference_ffmpeg, "-hide_banner", "-nostdin", "-v", "warning", "-y"] + hardware +
            ["-f", "lavfi", "-i", f"testsrc2=s=3840x2160:r=24:d={args.seconds}",
             "-vf", "format=p010le,hwupload=derive_device=vaapi,hwmap=derive_device=qsv,format=qsv",
             "-c:v", "hevc_qsv", "-preset", "veryfast", "-global_quality", "18", "-profile:v", "main10",
             "-color_primaries", "bt2020", "-colorspace", "bt2020nc", "-color_trc", "smpte2084",
             "-color_range", "tv", str(source)])
        for fixture in fixtures:
            filters = shlex.split(fixture["FilterArguments"])
            filters[1] = filters[1].replace("/media/subtitles.ass", str(directory / "subtitles.ass"))
            command = [args.ffmpeg, "-hide_banner", "-nostdin", "-v", "info", "-benchmark",
                       "-init_hw_device", "drm=dr:" + args.device, "-init_hw_device", "vaapi=va@dr",
                       "-init_hw_device", "qsv=qs@va", "-init_hw_device", "vulkan=vk@va",
                       "-filter_hw_device", "vk", "-hwaccel", "vaapi", "-hwaccel_device", "va",
                       "-hwaccel_output_format", "vaapi", "-i", str(source)] + filters
            if filters[0] == "-vf":
                command += ["-map", "0:v:0"]
            command += ["-an", "-copyts", "-avoid_negative_ts", "disabled", "-fps_mode", "passthrough",
                        "-c:v", "av1_qsv"] + shlex.split(fixture["ColorArguments"])
            command += ["-progress", "pipe:1", "-nostats", "-f", "null", "-"]
            start = time.monotonic()
            result = run(command)
            elapsed = time.monotonic() - start
            frames = [int(x) for x in re.findall(rb"^frame=(\d+)$", result.stdout, re.MULTILINE)]
            if not frames or frames[-1] != args.seconds * 24:
                raise RuntimeError(f"Unexpected frame count: {frames[-1:]}")
            diagnostic = result.stderr.decode(errors="replace")
            cpu = re.search(r"utime=([\d.]+)s stime=([\d.]+)s", diagnostic)
            item = {"width": fixture["OutputWidth"], "height": fixture["OutputHeight"],
                    "subtitle": fixture["Subtitle"], "bitrate": fixture["OutputVideoBitrate"],
                    "frames": frames[-1], "elapsed_seconds": round(elapsed, 3),
                    "fps": round(frames[-1] / elapsed, 2), "realtime_factor": round(args.seconds / elapsed, 2),
                    "cpu_seconds": round(sum(float(x) for x in cpu.groups()), 3) if cpu else None}
            results.append(item)
            print(json.dumps(item), file=sys.stderr, flush=True)
    print(json.dumps({"source": "synthetic moving 4K24 HEVC", "encoder_preset": "from exported arguments",
                      "includes_initialization": True, "results": results}))


if __name__ == "__main__":
    main()
