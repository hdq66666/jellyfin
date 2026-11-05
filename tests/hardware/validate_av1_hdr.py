#!/usr/bin/env python3
"""Exercise exported Jellyfin AV1 HDR graphs on the target Linux GPU.

Both fixture files must come from the Jellyfin tests. The reference FFmpeg must
have libx265; the FFmpeg under test must include the HDR libplacebo patches.
All generated media is temporary. Progress and diagnostics go to stderr; stdout
contains a single JSON result.
"""

import argparse
from array import array
from fractions import Fraction
import json
import math
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import tempfile
import time
import traceback

sys.dont_write_bytecode = True
from subtitle_fixtures import write_subtitles


TRANSFERS = ("smpte2084", "arib-std-b67")
SUBTITLES = (None, "hdmv_pgs_subtitle", "srt", "ass")
FPS = 24
MASTERING_MAX_UNITS, MASTERING_MIN_UNITS = 10000000, 1
MASTERING = ("G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)"
             f"L({MASTERING_MAX_UNITS},{MASTERING_MIN_UNITS})")
METADATA_KEYS = {
    "Mastering display metadata": ("red_x", "red_y", "green_x", "green_y", "blue_x", "blue_y",
                                   "white_point_x", "white_point_y", "min_luminance", "max_luminance"),
    "Content light level metadata": ("max_content", "max_average"),
}
MDCV_TOLERANCES = (Fraction(1, 65536),) * 8 + (Fraction(1, 16384), Fraction(1, 256))


class ValidationError(RuntimeError):
    pass


def check(condition, message):
    if not condition:
        raise ValidationError(message)


def progress(message):
    print(message, file=sys.stderr, flush=True)


def run(command, *, timeout=120, expect_failure=False):
    try:
        result = subprocess.run(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, timeout=timeout, check=False)
    except subprocess.TimeoutExpired as exc:
        raise ValidationError(f"Command timed out after {timeout}s: {shlex.join(command)}") from exc
    if not expect_failure and result.returncode:
        raise ValidationError(f"Command failed ({result.returncode}): {shlex.join(command)}\n"
                              + result.stderr.decode(errors="replace")[-10000:])
    return result


def probe(ffprobe, path):
    result = run([ffprobe, "-v", "error", "-select_streams", "v:0", "-show_streams",
                  "-show_frames", "-count_frames", "-of", "json", str(path)])
    return json.loads(result.stdout)


def device_args(device):
    return ["-init_hw_device", f"drm=drm:{device}", "-init_hw_device", "vaapi=va@drm",
            "-init_hw_device", "qsv=qs@va", "-init_hw_device", "vulkan=vk@va",
            "-filter_hw_device", "vk"]


def decode_args():
    return ["-hwaccel", "vaapi", "-hwaccel_device", "va", "-hwaccel_output_format", "vaapi"]


def ffmpeg_base(executable):
    return [executable, "-hide_banner", "-nostdin", "-loglevel", "warning", "-y"]


def x265_args(transfer):
    params = ("lossless=1:log-level=error:pools=1:frame-threads=1:"
              f"colorprim=bt2020:transfer={transfer}:colormatrix=bt2020nc:range=limited:"
              f"master-display={MASTERING}:max-cll=1000,400")
    return ["-c:v", "libx265", "-preset", "ultrafast", "-x265-params", params,
            "-pix_fmt", "yuv420p10le", "-color_primaries", "bt2020", "-color_trc", transfer,
            "-colorspace", "bt2020nc", "-color_range", "tv"]


def make_source(ffmpeg, directory, transfer, seconds=3, width=1280, height=720, pgs=True):
    destination = directory / f"source-{transfer}-{seconds}-{width}.mkv"
    color = f"color=gray:size={width}x{height}:rate={FPS}:duration={seconds}"
    command = ffmpeg_base(ffmpeg) + ["-f", "lavfi", "-i", color]
    if pgs:
        command += ["-f", "sup", "-i", str(directory / "subtitles.sup")]
    command += ["-map", "0:v:0"]
    if pgs:
        command += ["-map", "1:s:0", "-c:s", "copy"]
    command += ["-vf", "format=yuv420p10le,geq=lum=512:cb=512:cr=512"]
    command += x265_args(transfer) + [str(destination)]
    run(command, timeout=180)
    return destination


def extract_pgs(ffmpeg, source, destination):
    command = ffmpeg_base(ffmpeg) + ["-copyts", "-i", str(source), "-map", "0:1", "-c:s", "copy",
                                     "-avoid_negative_ts", "disabled", "-f", "sup", str(destination)]
    run(command)
    check(destination.is_file() and destination.stat().st_size > 0, "PGS extraction produced an empty cache")


def fixture_args(fixture, directory):
    args = shlex.split(fixture["FilterArguments"])
    check(len(args) == 2 and args[0] in ("-vf", "-filter_complex"),
          f"Unexpected FilterArguments for {fixture['Transfer']}/{fixture['Subtitle']}: {args!r}")
    subtitle = fixture["Subtitle"]
    if subtitle in ("srt", "ass"):
        placeholder = f"/media/subtitles.{subtitle}"
        check(args[1].count(placeholder) == 1, f"Missing or repeated subtitle path: {placeholder}")
        path = str(directory / f"subtitles.{subtitle}")
        check(re.fullmatch(r"/[A-Za-z0-9_./-]+", path) is not None, "Unsafe temporary filter path")
        args[1] = args[1].replace(placeholder, path)
    if subtitle == "hdmv_pgs_subtitle":
        check(re.search(r"\[1:0\]\s*scale", args[1]) is not None,
              "PGS fixture must use the extracted subtitle input [1:0]")
        check("[0:0]" in args[1], "PGS fixture is missing the video stream [0:0]")
    return args


def compare_metadata(kind, expected, actual, context):
    tolerances = MDCV_TOLERANCES if kind == "Mastering display metadata" else (Fraction(0),) * 2
    for key, before, after, tolerance in zip(METADATA_KEYS[kind], expected, actual, tolerances):
        check(abs(before - after) <= tolerance,
              f"{context} {kind} {key} changed: {before} -> {after} (tolerance {tolerance})")


def read_metadata(probe_result, reference=None):
    found = {}
    for item in probe_result.get("streams", []) + probe_result.get("frames", []):
        for side_data in item.get("side_data_list", []):
            kind = side_data.get("side_data_type")
            if kind not in METADATA_KEYS:
                continue
            try:
                values = tuple(Fraction(str(side_data[key])) for key in METADATA_KEYS[kind])
            except (KeyError, ValueError, ZeroDivisionError) as exc:
                raise ValidationError(f"Invalid {kind}: {side_data}") from exc
            if reference is not None and kind in reference:
                compare_metadata(kind, reference[kind], values, "Output")
            if kind in found:
                compare_metadata(kind, found[kind], values, "Inconsistent")
            else:
                found[kind] = values
    return found


def verify_metadata(source_probe, output_probe, transfer):
    source = read_metadata({"frames": source_probe.get("frames", [])})
    actual_stream = read_metadata({"streams": output_probe.get("streams", [])}, source)
    actual_frames = read_metadata({"frames": output_probe.get("frames", [])}, source)
    read_metadata(output_probe)
    status = {}
    for kind in METADATA_KEYS:
        check(kind in source, f"Synthetic source frames are missing {kind}")
        check(transfer != "smpte2084" or kind in actual_frames, f"PQ decoded frames are missing {kind}")
        status[kind] = {}
        for location, actual in (("container", actual_stream), ("frames", actual_frames)):
            if kind not in actual:
                status[kind][location] = "absent"
            else:
                compare_metadata(kind, source[kind], actual[kind], location)
                status[kind][location] = "preserved"
    return status


def verify_video(probe_result, transfer, frame_count, source_first_pts=None):
    check(len(probe_result.get("streams", [])) == 1, "Expected exactly one video stream")
    stream = probe_result["streams"][0]
    expected = {"codec_name": "av1", "width": 640, "height": 360, "pix_fmt": "yuv420p10le",
                "color_range": "tv", "color_primaries": "bt2020", "color_space": "bt2020nc",
                "color_transfer": transfer}
    for key, value in expected.items():
        check(stream.get(key) == value, f"stream {key}: expected {value}, got {stream.get(key)}")
    check(int(stream.get("nb_read_frames", -1)) == frame_count,
          f"Expected {frame_count} frames, got {stream.get('nb_read_frames')}")
    frames = probe_result.get("frames", [])
    check(len(frames) == frame_count, f"Expected {frame_count} decoded frames, got {len(frames)}")
    first_pts = None
    previous = None
    for index, frame in enumerate(frames):
        for key in ("pix_fmt", "color_range", "color_primaries", "color_space", "color_transfer"):
            check(frame.get(key) == expected[key], f"frame {index} {key}: {frame.get(key)}")
        try:
            pts = float(frame["best_effort_timestamp_time"])
        except (KeyError, ValueError) as exc:
            raise ValidationError(f"frame {index} is missing a usable PTS") from exc
        first_pts = pts if first_pts is None else first_pts
        check(previous is None or pts > previous, f"PTS is not monotonic at frame {index}")
        check(abs((pts - first_pts) - index / FPS) <= 0.0015,
              f"PTS differs from {FPS} fps at frame {index}: {pts}")
        previous = pts
    if source_first_pts is not None:
        check(abs(first_pts - source_first_pts) <= 0.0015,
              f"Output first PTS {first_pts} differs from source first PTS {source_first_pts}")


def parse_hls(codec, video_range):
    match = re.fullmatch(r"av01\.(\d)\.(\d{2})([MH])\.(\d{2})(?:\.(\d)\.(\d{3})\.(\d{2})\.(\d{2})\.(\d{2})\.(\d))?", codec)
    check(match is not None, f"Invalid AV1 HLS codec string: {codec}")
    profile, level, tier, depth, mono, chroma, primaries, transfer, matrix, range_flag = match.groups()
    # ISO/IEC 14496-15 defaults for omitted AV1 codec string fields.
    mono, chroma, primaries, transfer, matrix, range_flag = (
        (mono, chroma, primaries, transfer, matrix, range_flag)
        if mono is not None else ("0", "110", "01", "01", "01", "0"))
    return {"seq_profile": int(profile), "seq_level_idx[0]": int(level),
            "seq_tier[0]": int(tier == "H"), "depth": int(depth), "mono_chrome": int(mono),
            "chroma": chroma, "color_primaries": int(primaries),
            "transfer_characteristics": int(transfer), "matrix_coefficients": int(matrix),
            "color_range": int(range_flag), "video_range": video_range}


def verify_headers(ffmpeg, path, transfer, hls):
    result = run([ffmpeg, "-hide_banner", "-nostdin", "-loglevel", "info", "-i", str(path),
                  "-map", "0:v:0", "-c:v", "copy", "-bsf:v", "trace_headers", "-f", "null", "-"])
    output = result.stderr.decode(errors="replace")
    fields = {}
    for line in output.splitlines():
        match = re.search(r"\] \d+\s+([A-Za-z0-9_]+(?:\[\d+\])?)\s+[01]+\s+=\s+(\d+)\s*$", line)
        if match:
            fields.setdefault(match.group(1), set()).add(int(match.group(2)))
    required = ("seq_profile", "seq_level_idx[0]", "seq_tier[0]", "high_bitdepth", "mono_chrome",
                "color_primaries", "transfer_characteristics", "matrix_coefficients", "color_range",
                "chroma_sample_position")
    for key in required:
        check(key in fields, f"AV1 trace missing {key}")
        check(len(fields[key]) == 1, f"AV1 trace contains inconsistent {key}: {fields[key]}")
    values = {key: next(iter(fields[key])) for key in required}
    codec = hls["CodecString"]
    advertised = parse_hls(codec, hls["VideoRange"])
    expected_range = "PQ" if transfer == "smpte2084" else "HLG"
    check(advertised["video_range"] == expected_range,
          f"HLS VIDEO-RANGE {advertised['video_range']} does not match {transfer}")
    check(values["seq_profile"] == 0 and values["high_bitdepth"] == 1 and values["mono_chrome"] == 0,
          f"Unexpected AV1 Main 10-bit 4:2:0 header: {values}")
    check(values["seq_level_idx[0]"] == 15, f"Unexpected AV1 level: {values['seq_level_idx[0]']}")
    check(values["chroma_sample_position"] == 0, "AV1 chroma sample position is not 0")
    check(values["color_primaries"] == 9 and values["matrix_coefficients"] == 9 and
          values["transfer_characteristics"] == (16 if transfer == "smpte2084" else 18) and
          values["color_range"] == 0, f"Incorrect AV1 sequence color fields: {values}")
    for key in ("seq_profile", "seq_level_idx[0]", "seq_tier[0]", "mono_chrome",
                "color_primaries", "transfer_characteristics", "matrix_coefficients", "color_range"):
        check(advertised[key] == values[key], f"HLS codec {key} {advertised[key]} != bitstream {values[key]}")
    check(advertised["depth"] == 10 and advertised["chroma"] == f"11{values['chroma_sample_position']}",
          f"HLS depth/chroma differs from AV1 bitstream: {codec}")
    return {"codec_string": codec, "video_range": expected_range, "sequence_header": values}


def encode_command(ffmpeg, device, fixture, directory, source, output, subtitle_cache=None):
    args = fixture_args(fixture, directory)
    subtitle_args = shlex.split(fixture.get("SubtitleInputArguments", ""))
    if fixture["Subtitle"] == "hdmv_pgs_subtitle":
        placeholder = "/media/hdr-pgs.sup"
        check(subtitle_cache is not None and subtitle_cache.is_file(), "PGS subtitle cache is missing")
        check(sum(arg.count(placeholder) for arg in subtitle_args) == 1 and subtitle_args.count("-i") == 1,
              "PGS fixture must contain exactly one cached SUP input")
        subtitle_args = [arg.replace(placeholder, str(subtitle_cache)) for arg in subtitle_args]
    else:
        check(not subtitle_args, "Unexpected separate subtitle input for a non-PGS fixture")
    command = (ffmpeg_base(ffmpeg) + device_args(device) + decode_args() + ["-i", str(source)]
               + subtitle_args + args)
    if args[0] == "-vf":
        command += ["-map", "0:v:0"]
    command += ["-copyts", "-avoid_negative_ts", "disabled", "-an", "-fps_mode", "passthrough",
                "-c:v", "av1_qsv"]
    command += shlex.split(fixture["ColorArguments"]) + [str(output)]
    return command


def decoded_samples(ffmpeg, path, indices=(0, 10, 18, 30, 42), expect_color=False, expect_glyphs=False):
    expression = "+".join(f"eq(n\\,{index})" for index in indices)
    result = run([ffmpeg, "-hide_banner", "-nostdin", "-loglevel", "error", "-i", str(path),
                  "-vf", "select=" + expression, "-fps_mode", "passthrough", "-pix_fmt", "yuv420p10le",
                  "-f", "rawvideo", "pipe:1"])
    frame_bytes = 640 * 360 * 3
    check(len(result.stdout) == frame_bytes * len(indices), "Incorrect number of sampled subtitle frames")
    frames = []
    for index in range(len(indices)):
        values = array("H")
        start = index * frame_bytes
        values.frombytes(result.stdout[start:start + frame_bytes])
        if sys.byteorder != "little":
            values.byteswap()
        frames.append(values)
    baseline = frames[0]
    pixels = 640 * 360
    changed = [sum(abs(a - b) > 12 for a, b in zip(frame[:pixels], baseline[:pixels]))
               for frame in frames[1:]]
    check(changed[0] >= 60 and changed[2] >= 60, f"Subtitle events are missing: {changed}")
    check(changed[1] <= max(100, changed[0] // 10) and changed[3] <= max(100, changed[2] // 10),
          f"Subtitles were not cleared between/after events: {changed}")
    result = {"active_and_clear_changed_pixels": changed, "sample_frames": list(indices)}
    if expect_color:
        chroma_index = (235 // 2) * (640 // 2) + (360 // 2)
        u_index, v_index = pixels + chroma_index, pixels + pixels // 4 + chroma_index
        base_u, base_v = baseline[u_index], baseline[v_index]
        cyan_u, cyan_v = frames[1][u_index], frames[1][v_index]
        magenta_u, magenta_v = frames[3][u_index], frames[3][v_index]
        check(cyan_u > base_u + 4 and cyan_v < base_v - 4 and
              magenta_u > base_u + 4 and magenta_v > base_v + 4,
              f"Colored subtitle chroma is missing or incorrect: base={base_u, base_v}, "
              f"cyan={cyan_u, cyan_v}, magenta={magenta_u, magenta_v}")
        result["colored_subtitle_chroma"] = {"base": [base_u, base_v], "cyan": [cyan_u, cyan_v],
                                               "magenta": [magenta_u, magenta_v]}
    if expect_glyphs:
        glyph_pixels = [sum(abs(frame[y * 640 + x] - baseline[y * 640 + x]) > 12
                            for y in range(145, 200) for x in range(70, 580))
                        for frame in (frames[1], frames[3])]
        check(all(count >= 50 for count in glyph_pixels), f"ASS glyphs or shadows missing: {glyph_pixels}")
        result["glyph_changed_pixels"] = glyph_pixels
    return result


def verify_subtitle_frame_timing(ffmpeg, path):
    # The opaque 100x40 white block spans this 4x4 region in both ASS and PGS.
    command = [ffmpeg, "-hide_banner", "-nostdin", "-loglevel", "error", "-i", str(path),
               "-vf", "crop=4:4:120:235:exact=1", "-fps_mode", "passthrough",
               "-pix_fmt", "yuv420p10le", "-f", "rawvideo", "pipe:1"]
    data = run(command).stdout
    samples_per_frame = 4 * 4 * 3 // 2
    frame_count = 3 * FPS
    check(len(data) == frame_count * samples_per_frame * 2,
          f"Incorrect subtitle timing ROI length: {len(data)}")
    samples = array("H")
    samples.frombytes(data)
    if sys.byteorder != "little":
        samples.byteswap()
    means = [sum(samples[i * samples_per_frame:i * samples_per_frame + 16]) / 16
             for i in range(frame_count)]
    baseline = means[0]
    active = [index for index, mean in enumerate(means) if mean - baseline > 12]
    expected = list(range(5, 16)) + list(range(24, 36))
    check(active == expected, f"Subtitle ROI active frames {active}, expected {expected}; "
          f"luma deltas {[round(value - baseline, 2) for value in means]}")
    return {"active_frames": active, "baseline_luma": baseline,
            "minimum_active_delta": round(min(means[i] - baseline for i in expected), 2)}


def pq_decode(code):
    m1, m2 = 2610 / 16384, 2523 / 32
    c1, c2, c3 = 3424 / 4096, 2413 / 128, 2392 / 128
    powered = code ** (1 / m2)
    return 10000 * (max(powered - c1, 0) / (c2 - c3 * powered)) ** (1 / m1)


def pq_encode(nits):
    m1, m2 = 2610 / 16384, 2523 / 32
    c1, c2, c3 = 3424 / 4096, 2413 / 128, 2392 / 128
    powered = (nits / 10000) ** m1
    return ((c1 + c2 * powered) / (1 + c3 * powered)) ** m2


def hlg_decode(code):
    a, b = 0.17883277, 1 - 4 * 0.17883277
    c = 0.5 - a * math.log(4 * a)
    maximum, minimum = MASTERING_MAX_UNITS / 10000, MASTERING_MIN_UNITS / 10000
    gamma = 1.2 * 1.111 ** math.log2(maximum / 1000)
    beta = math.sqrt(3 * (minimum / maximum) ** (1 / gamma))
    corrected = (1 - beta) * code + beta
    scene = (corrected * corrected / 3 if corrected <= 0.5
             else (math.exp((corrected - c) / a) + b) / 12)
    return maximum * scene ** gamma


def hlg_encode(nits):
    a, b = 0.17883277, 1 - 4 * 0.17883277
    c = 0.5 - a * math.log(4 * a)
    maximum, minimum = MASTERING_MAX_UNITS / 10000, MASTERING_MIN_UNITS / 10000
    gamma = 1.2 * 1.111 ** math.log2(maximum / 1000)
    beta = math.sqrt(3 * (minimum / maximum) ** (1 / gamma))
    scene = (nits / maximum) ** (1 / gamma)
    corrected = math.sqrt(3 * scene) if scene <= 1 / 12 else a * math.log(12 * scene - b) + c
    return (corrected - beta) / (1 - beta)


def expected_blend(transfer, y, alpha):
    decode, encode = (pq_decode, pq_encode) if transfer == "smpte2084" else (hlg_decode, hlg_encode)
    nits = decode((y - 64) / 876)
    return 64 + 876 * encode(nits * (1 - alpha / 255) + 203 * alpha / 255)


def raw_16(values):
    result = array("H", values)
    if sys.byteorder != "little":
        result.byteswap()
    return result.tobytes()


def make_blend_sources(ffmpeg, directory, transfer):
    cases = [(y, round(alpha * 255)) for y in (64, 128, 512, 723, 940)
             for alpha in (0, 0.25, 0.5, 0.75, 1)]
    cases += [(y, 0) for y in range(64, 128)]
    raw_video = directory / f"blend-source-{transfer}.yuv"
    raw_subs = directory / f"blend-subtitles-{transfer}.bgra"
    encoded = directory / f"blend-source-{transfer}.mp4"
    with raw_video.open("wb") as video, raw_subs.open("wb") as subs:
        chroma = raw_16([512] * (32 * 32 * 2))
        for y, alpha in cases:
            video.write(raw_16([y] * (64 * 64)))
            video.write(chroma)
            subs.write(bytes((255, 255, 255, alpha)) * (64 * 64))
    command = ffmpeg_base(ffmpeg) + ["-f", "rawvideo", "-pix_fmt", "yuv420p10le", "-s", "64x64",
                                     "-r", str(FPS), "-i", str(raw_video)]
    command += x265_args(transfer) + ["-video_track_timescale", str(FPS), str(encoded)]
    run(command)
    return cases, encoded, raw_subs


def blend_command(ffmpeg, device, fixture, source, subs, output, *, negative=False):
    overlay = fixture["Overlay"][0]
    check("require_linear_blend=1" in overlay, "Fixture does not require linear blending")
    if fixture["Transfer"] == "arib-std-b67":
        check("hlg_reference_peak=1000" in overlay, "Fixture is missing the HLG 1000-nit reference")
    if negative:
        overlay += ":disable_fbos=1"
    graph = ("[0:v]hwmap=derive_device=drm:mode=read+direct,format=drm_prime[main];"
             "[1:v]setparams=colorspace=gbr:color_primaries=bt709:color_trc=bt709:"
             "range=pc:alpha_mode=straight[sub];[main][sub]" + overlay +
             ",format=vulkan,hwmap=derive_device=vaapi:mode=read+direct,format=vaapi,"
             "hwdownload,format=p010le,format=yuv420p10le[out]")
    return (ffmpeg_base(ffmpeg) + device_args(device) + decode_args() + ["-i", str(source),
            "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", "64x64", "-framerate", str(FPS),
            "-i", str(subs), "-filter_complex", graph, "-map", "[out]", "-fps_mode", "passthrough",
            "-pix_fmt", "yuv420p10le", "-f", "rawvideo", str(output)])


def validate_blend(ffmpeg, reference_ffmpeg, device, fixture, directory):
    transfer = fixture["Transfer"]
    cases, source, subs = make_blend_sources(reference_ffmpeg, directory, transfer)
    output = directory / f"blend-output-{transfer}.yuv"
    run(blend_command(ffmpeg, device, fixture, source, subs, output))
    data = output.read_bytes()
    negative_output = directory / f"blend-negative-{transfer}.yuv"
    result = run(blend_command(ffmpeg, device, fixture, source, subs, negative_output, negative=True),
                 expect_failure=True)
    message = result.stderr.decode(errors="replace").lower()
    check(result.returncode != 0 and ("linear blend" in message or "disable_fbos" in message),
          f"Expected explicit linear-blend failure, got exit {result.returncode}: {message[-2000:]}")
    pixels = 64 * 64
    samples_per_frame = pixels * 3 // 2
    check(len(data) == len(cases) * samples_per_frame * 2,
          f"Incorrect raw blend output size: {len(data)}")
    samples = array("H")
    samples.frombytes(data)
    if sys.byteorder != "little":
        samples.byteswap()
    max_y_error = 0.0
    max_chroma_error = 0
    matrix = []
    for index, (y, alpha) in enumerate(cases):
        frame = samples[index * samples_per_frame:(index + 1) * samples_per_frame]
        expected = expected_blend(transfer, y, alpha)
        y_min, y_max = min(frame[:pixels]), max(frame[:pixels])
        y_error = max(abs(y_min - expected), abs(y_max - expected))
        chroma_error = max(abs(value - 512) for value in frame[pixels:])
        check(y_error <= 2, f"{transfer} Y={y} alpha={alpha}: output Y_min={y_min}, Y_max={y_max}, "
              f"expected={expected:.4f}, max luma error={y_error:.4f}")
        check(chroma_error <= 2, f"{transfer} Y={y} alpha={alpha}: max chroma error {chroma_error}")
        matrix.append({"source_y": y, "alpha_8bit": alpha, "expected_y": round(expected, 4),
                       "y_min": y_min, "y_max": y_max})
        max_y_error = max(max_y_error, y_error)
        max_chroma_error = max(max_chroma_error, chroma_error)
    return {"transfer": transfer, "cases": len(cases), "max_y_error": round(max_y_error, 4),
            "max_uv_error": max_chroma_error, "incompatible_options_rejected": True, "matrix": matrix}


def rss_kib(pid):
    try:
        status = Path(f"/proc/{pid}/status").read_text()
        match = re.search(r"^VmRSS:\s+(\d+)\s+kB", status, re.MULTILINE)
        return int(match.group(1)) if match else None
    except (FileNotFoundError, ProcessLookupError):
        return None


def run_stress(command, directory, timeout):
    stderr_path = directory / "stress.stderr"
    samples = []
    start = time.monotonic()
    with stderr_path.open("wb") as stderr:
        process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=stderr)
        try:
            next_sample = start
            while process.poll() is None:
                now = time.monotonic()
                if now - start > timeout:
                    process.kill()
                    process.wait()
                    raise ValidationError(f"Sparse subtitle stress exceeded {timeout}s")
                if now >= next_sample:
                    rss = rss_kib(process.pid)
                    if rss is not None:
                        samples.append({"seconds": round(now - start, 2), "rss_kib": rss})
                    next_sample = now + 1
                time.sleep(0.1)
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
    elapsed = time.monotonic() - start
    check(process.returncode == 0,
          f"Sparse subtitle stress failed ({process.returncode}): " + stderr_path.read_text(errors="replace")[-10000:])
    check(samples, "Could not record stress-process RSS from /proc")
    return {"elapsed_seconds": round(elapsed, 2), "peak_rss_kib": max(item["rss_kib"] for item in samples),
            "rss_samples": samples}


def main(summary):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixtures", required=True, help="C#-exported production filter fixture JSON")
    parser.add_argument("--hls-fixtures", required=True, help="C#-exported production HLS codec/range JSON")
    parser.add_argument("--ffmpeg", default="/usr/lib/jellyfin-ffmpeg/ffmpeg", help="FFmpeg under test")
    parser.add_argument("--reference-ffmpeg", default="/usr/lib/jellyfin-ffmpeg/ffmpeg")
    parser.add_argument("--ffprobe", default="/usr/lib/jellyfin-ffmpeg/ffprobe")
    parser.add_argument("--device", default="/dev/dri/renderD128")
    parser.add_argument("--subtitles", nargs="+", choices=("none", "srt", "ass", "hdmv_pgs_subtitle"),
                        default=("none", "srt", "ass", "hdmv_pgs_subtitle"))
    parser.add_argument("--transfers", nargs="+", choices=TRANSFERS, default=TRANSFERS)
    parser.add_argument("--skip-blend", action="store_true")
    parser.add_argument("--skip-stress", action="store_true")
    parser.add_argument("--stress-seconds", type=int, default=90, help="Must be at least 62 seconds")
    parser.add_argument("--stress-timeout", type=int, default=90)
    parser.add_argument("--work-dir", type=Path, help="Parent directory for auto-cleaned temporary data")
    parser.add_argument("--log-dir", type=Path, help="Optionally retain the stress FFmpeg log")
    args = parser.parse_args()
    check(args.skip_stress or args.stress_seconds >= 62, "Stress source must include the 60-61s subtitle")
    check(args.stress_timeout > 0, "Stress timeout must be positive")
    fixtures = json.loads(Path(args.fixtures).read_text())
    hls = json.loads(Path(args.hls_fixtures).read_text())
    lookup = {(f["Transfer"], f["Subtitle"]): f for f in fixtures}
    check(len(fixtures) == len(lookup) == len(TRANSFERS) * len(SUBTITLES), "Expected eight unique filter fixtures")
    check(all((transfer, subtitle) in lookup for transfer in TRANSFERS for subtitle in SUBTITLES),
          "Missing expected transfer/subtitle fixture")
    check(all(transfer in hls for transfer in TRANSFERS), "Missing HLS fixtures")
    selected_subtitles = {None if subtitle == "none" else subtitle for subtitle in args.subtitles}
    work_dir = args.work_dir.resolve() if args.work_dir else Path(tempfile.gettempdir()).resolve()
    check(work_dir.is_dir(), f"Temporary parent directory does not exist: {work_dir}")
    with tempfile.TemporaryDirectory(prefix="jellyfin-av1-hdr-", dir=work_dir) as temp:
        directory = Path(temp)
        os.environ["XDG_CACHE_HOME"] = str(directory)
        os.environ["NEO_CACHE_DIR"] = str(directory)
        write_subtitles(directory)
        for transfer in args.transfers:
            progress(f"Creating {transfer} source and validating exported production graphs")
            source = make_source(args.reference_ffmpeg, directory, transfer)
            source_probe = probe(args.ffprobe, source)
            source_first_pts = float(source_probe["frames"][0]["best_effort_timestamp_time"])
            subtitle_cache = None
            if "hdmv_pgs_subtitle" in selected_subtitles:
                subtitle_cache = directory / f"cached-pgs-{transfer}.sup"
                extract_pgs(args.reference_ffmpeg, source, subtitle_cache)
            for subtitle in SUBTITLES:
                if subtitle not in selected_subtitles:
                    continue
                fixture = lookup[(transfer, subtitle)]
                output = directory / f"output-{transfer}-{subtitle}.mp4"
                run(encode_command(args.ffmpeg, args.device, fixture, directory, source, output, subtitle_cache))
                output_probe = probe(args.ffprobe, output)
                verify_video(output_probe, transfer, 3 * FPS, source_first_pts)
                metadata = verify_metadata(source_probe, output_probe, transfer)
                headers = verify_headers(args.ffmpeg, output, transfer, hls[transfer])
                timing = (decoded_samples(args.reference_ffmpeg, output,
                                          expect_color=subtitle in ("hdmv_pgs_subtitle", "ass"),
                                          expect_glyphs=subtitle == "ass")
                          if subtitle else None)
                if subtitle in ("hdmv_pgs_subtitle", "ass"):
                    timing["white_roi"] = verify_subtitle_frame_timing(args.reference_ffmpeg, output)
                summary["encoded_graphs"].append({"transfer": transfer, "subtitle": subtitle,
                                                   "frames": 3 * FPS, "headers": headers,
                                                   "metadata": metadata, "timing": timing})
            if not args.skip_blend:
                progress(f"Checking {transfer} linear-light blend and near-black ramp")
                summary["linear_blend"].append(validate_blend(args.ffmpeg, args.reference_ffmpeg, args.device,
                                                                lookup[(transfer, "srt")], directory))
        if not args.skip_stress:
            progress(f"Running {args.stress_seconds}s sparse subtitle natural-EOF stress")
            stress_source = make_source(args.reference_ffmpeg, directory, "smpte2084", args.stress_seconds,
                                        width=640, height=360, pgs=False)
            (directory / "subtitles.srt").write_text(
                "1\n00:00:01,000 --> 00:00:02,000\nFIRST\n\n"
                "2\n00:01:00,000 --> 00:01:01,000\nSECOND\n", encoding="utf-8")
            stress_output = directory / "stress.mp4"
            command = encode_command(args.ffmpeg, args.device, lookup[("smpte2084", "srt")], directory,
                                     stress_source, stress_output)
            try:
                summary["stress"] = run_stress(command, directory, args.stress_timeout)
            finally:
                if args.log_dir and (directory / "stress.stderr").exists():
                    args.log_dir.mkdir(parents=True, exist_ok=True)
                    (args.log_dir / "stress.stderr").write_bytes((directory / "stress.stderr").read_bytes())
            verify_video(probe(args.ffprobe, stress_output), "smpte2084", args.stress_seconds * FPS)
            summary["stress"]["frames"] = args.stress_seconds * FPS
            summary["stress"]["timing"] = decoded_samples(args.reference_ffmpeg, stress_output,
                                                         (0, 36, 60, 1452, 1476))
    summary["status"] = "ok"


if __name__ == "__main__":
    summary = {"status": "running", "encoded_graphs": [], "linear_blend": [], "stress": None}
    try:
        main(summary)
    except Exception as exc:
        traceback.print_exc(file=sys.stderr)
        summary.update({"status": "failed", "error": str(exc)})
        print(json.dumps(summary, sort_keys=True))
        sys.exit(1)
    print(json.dumps(summary, sort_keys=True))
