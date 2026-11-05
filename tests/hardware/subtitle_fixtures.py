"""Synthetic text and PGS subtitles for hardware validation."""

import struct


SRT = """1
00:00:00,200 --> 00:00:00,650
WHITE TEXT

2
00:00:01,000 --> 00:00:01,500
SECOND WHITE TEXT
"""

ASS = r"""[Script Info]
ScriptType: v4.00+
PlayResX: 640
PlayResY: 360

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,24,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.20,0:00:00.65,Default,,0,0,0,,{\an7\pos(80,215)\bord0\shad0\1c&HFFFFFF&\1a&H00&\p1}m 0 0 l 100 0 100 40 0 40{\p0}
Dialogue: 0,0:00:00.20,0:00:00.65,Default,,0,0,0,,{\an7\pos(200,215)\bord0\shad0\1c&HFFFFFF&\1a&H80&\p1}m 0 0 l 100 0 100 40 0 40{\p0}
Dialogue: 0,0:00:00.20,0:00:00.65,Default,,0,0,0,,{\an7\pos(320,215)\bord0\shad0\1c&HFFFF00&\1a&H80&\p1}m 0 0 l 100 0 100 40 0 40{\p0}
Dialogue: 0,0:00:00.20,0:00:00.65,Default,,0,0,0,,{\an7\pos(440,225)\bord0\shad0\1c&H000000&\1a&H80&\p1}m 0 0 l 100 0 100 40 0 40{\p0}
Dialogue: 1,0:00:00.20,0:00:00.65,Default,,0,0,0,,{\an7\pos(430,215)\bord0\shad0\1c&HFFFFFF&\1a&H80&\p1}m 0 0 l 100 0 100 40 0 40{\p0}
Dialogue: 2,0:00:00.20,0:00:00.65,Default,,0,0,0,,{\an7\pos(80,155)\bord1\shad2\1c&HFFFFFF&\1a&H00&}WHITE TEXT
Dialogue: 2,0:00:00.20,0:00:00.65,Default,,0,0,0,,{\an7\pos(260,155)\bord1\shad2\1c&HFFFFFF&\1a&H80&}HALF WHITE
Dialogue: 0,0:00:01.00,0:00:01.50,Default,,0,0,0,,{\an7\pos(80,215)\bord0\shad0\1c&HFFFFFF&\1a&H00&\p1}m 0 0 l 100 0 100 40 0 40{\p0}
Dialogue: 0,0:00:01.00,0:00:01.50,Default,,0,0,0,,{\an7\pos(200,215)\bord0\shad0\1c&HFFFFFF&\1a&H80&\p1}m 0 0 l 100 0 100 40 0 40{\p0}
Dialogue: 0,0:00:01.00,0:00:01.50,Default,,0,0,0,,{\an7\pos(320,215)\bord0\shad0\1c&HFF00FF&\1a&H80&\p1}m 0 0 l 100 0 100 40 0 40{\p0}
Dialogue: 0,0:00:01.00,0:00:01.50,Default,,0,0,0,,{\an7\pos(440,225)\bord0\shad0\1c&H000000&\1a&H80&\p1}m 0 0 l 100 0 100 40 0 40{\p0}
Dialogue: 1,0:00:01.00,0:00:01.50,Default,,0,0,0,,{\an7\pos(430,215)\bord0\shad0\1c&HFFFFFF&\1a&H80&\p1}m 0 0 l 100 0 100 40 0 40{\p0}
Dialogue: 2,0:00:01.00,0:00:01.50,Default,,0,0,0,,{\an7\pos(80,155)\bord1\shad2\1c&HFFFFFF&\1a&H00&}SECOND WHITE TEXT
Dialogue: 2,0:00:01.00,0:00:01.50,Default,,0,0,0,,{\an7\pos(340,155)\bord1\shad2\1c&HFF00FF&\1a&H80&}MAGENTA
"""


def _segment(ms, kind, payload):
    return b"PG" + struct.pack(">IIBH", ms * 90, ms * 90, kind, len(payload)) + payload


def _display(ms, number, second):
    width, height = 480, 80
    # Opaque, translucent, colored, and overlapping subtitle regions.
    rows = [[0] * width for _ in range(height)]
    for row in rows[15:55]:
        row[0:100] = [1] * 100
        row[120:220] = [2] * 100
        row[240:340] = [6 if second else 3] * 100
    for row in rows[25:65]:
        row[360:460] = [4] * 100
    for row in rows[15:55]:
        for x in range(350, 450):
            row[x] = 5 if row[x] == 4 else 2
    bitmap = bytearray()
    for row in rows:
        x = 0
        while x < width:
            value = row[x]
            end = x + 1
            while end < width and row[end] == value and end - x < 16383:
                end += 1
            length = end - x
            if value and length == 1:
                bitmap.append(value)
            elif length < 64:
                bitmap.extend((0, length | (0x80 if value else 0)))
                if value:
                    bitmap.append(value)
            else:
                bitmap.extend((0, (length >> 8) | (0xC0 if value else 0x40), length & 0xFF))
                if value:
                    bitmap.append(value)
            x = end
        bitmap.extend((0, 0))
    composition = struct.pack(">HHBHBBBBHBBHH", 640, 360, 0x10, number, 0x80, 0, 0, 1, 1, 0, 0, 80, 200)
    window = struct.pack(">BBHHHH", 1, 0, 80, 200, width, height)
    entries = [(0, 16, 128, 128, 0), (1, 235, 128, 128, 255),
               (2, 235, 128, 128, 128), (3, 170, 16, 166, 128),
               (4, 16, 128, 128, 128), (5, 162, 128, 128, 192),
               (6, 106, 222, 202, 128)]
    palette = bytes([0, 0]) + b"".join(bytes(entry) for entry in entries)
    obj = struct.pack(">HBB", 1, 0, 0xC0) + (len(bitmap) + 4).to_bytes(3, "big")
    obj += struct.pack(">HH", width, height) + bitmap
    return b"".join((_segment(ms, 0x16, composition), _segment(ms, 0x17, window),
                    _segment(ms, 0x14, palette), _segment(ms, 0x15, obj),
                    _segment(ms, 0x80, b"")))


def _clear(ms, number):
    payload = struct.pack(">HHBHBBBB", 640, 360, 0x10, number, 0, 0, 0, 0)
    return _segment(ms, 0x16, payload) + _segment(ms, 0x80, b"")


def write_subtitles(directory):
    (directory / "subtitles.srt").write_text(SRT, encoding="utf-8")
    (directory / "subtitles.ass").write_text(ASS, encoding="utf-8")
    (directory / "subtitles.sup").write_bytes(
        _clear(0, 0) + _display(200, 1, False) + _clear(650, 2) +
        _display(1000, 3, True) + _clear(1500, 4))
