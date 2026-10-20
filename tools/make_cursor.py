# -*- coding: utf-8 -*-
"""绘画态光标生成器（纯 stdlib）：pen.cur / eraser.cur。

用法:  python tools/make_cursor.py [pen|eraser] [输出.cur]
设计:  32x32，4x 超采样抗锯齿。
       pen:    斜置画笔（笔尖朝左上=热点(2,2)），浅杆深边红尖
       eraser: 斜置橡皮块（热点左上(4,4)），浅色块+珊瑚红腰带
格式:  非压缩 32bpp CUR（BITMAPINFOHEADER + 自底向上 BGRA + AND 掩码），
       WPF 的 Cursor(Stream) 直接可用（PNG-in-CUR 对光标加载器不可靠）。
"""
import math
import struct
import sys

SIZE = 32
OUTLINE = (0x1A, 0x1A, 0x24, 255)
PEN_HOT = (2, 2)
ERASER_HOT = (4, 4)
BODY_A = (0xF2, 0xF2, 0xF7, 255)   # 笔杆亮端
BODY_B = (0xC8, 0xC8, 0xD4, 255)   # 笔杆暗端
NIB = (0xE8, 0x2D, 0x43, 255)      # 珊瑚红
NIB2 = (0xFF, 0x6B, 0x77, 255)


def stamp(px, n, x, y, r, color):
    """以 (x,y) 为圆心冲压半径 r 的圆（ss 坐标，硬冲压靠超采样抗锯齿）。"""
    y0 = max(0, int(y - r))
    y1 = min(n - 1, int(y + r) + 1)
    for yy in range(y0, y1):
        dy = yy + 0.5 - y
        d = r * r - dy * dy
        if d <= 0:
            continue
        half = math.sqrt(d)
        for xx in range(max(0, int(x - half)), min(n - 1, int(x + half)) + 1):
            px[(n * yy + xx) * 4: (n * yy + xx) * 4 + 4] = bytes(color)


def render(kind="pen"):
    s = 4  # 超采样
    n = SIZE * s
    px = bytearray(n * n * 4)

    if kind == "pen":
        # 笔的轴线（32 逻辑坐标）：笔尖(3,3) → 笔杆尾(24,24)
        p0, p1 = (3.0, 3.0), (24.0, 24.0)
        steps = 90
        for i in range(steps + 1):
            t = i / steps
            x = p0[0] + (p1[0] - p0[0]) * t
            y = p0[1] + (p1[1] - p0[1]) * t
            # 半径沿笔杆变粗；尖端另有红色笔头
            r = (1.1 + 2.2 * t) * s
            c = tuple(int(a + (b - a) * t) for a, b in zip(BODY_A[:3], BODY_B[:3])) + (255,)
            stamp(px, n, x * s, y * s, r + 1.6 * s, OUTLINE)   # 描边（更粗，先画）
            stamp(px, n, x * s, y * s, r, c)
        # 笔尖（红色小头，盖在笔杆起点上）
        stamp(px, n, 4.2 * s, 4.2 * s, (1.1 + 1.6) * s + 1.6 * s, OUTLINE)
        stamp(px, n, 4.2 * s, 4.2 * s, 1.1 * s + 1.6 * s, NIB)
        stamp(px, n, 3.4 * s, 3.4 * s, 0.8 * s, NIB2)
    else:
        # 橡皮：斜置圆角块（左上 4,4 → 右下 26,26 方向的粗短条）
        p0, p1 = (6.0, 6.0), (24.0, 24.0)
        steps = 40
        for i in range(steps + 1):
            t = i / steps
            x = p0[0] + (p1[0] - p0[0]) * t
            y = p0[1] + (p1[1] - p0[1]) * t
            r = 4.6 * s
            stamp(px, n, x * s, y * s, r + 1.6 * s, OUTLINE)
            c = tuple(int(a + (b - a) * t) for a, b in zip(BODY_A[:3], BODY_B[:3])) + (255,)
            stamp(px, n, x * s, y * s, r, c)
        # 珊瑚红腰带（中段）
        for i in range(steps + 1):
            t = i / steps
            if not 0.42 <= t <= 0.68:
                continue
            x = p0[0] + (p1[0] - p0[0]) * t
            y = p0[1] + (p1[1] - p0[1]) * t
            stamp(px, n, x * s, y * s, 4.6 * s - 0.6 * s, NIB2 if t < 0.55 else NIB)

    # 4x 盒式降采样 → BGRA 自顶向下
    rows = []
    for y in range(SIZE):
        row = bytearray()
        for x in range(SIZE):
            r = g = b = a = 0
            for sy in range(s):
                base = (n * (y * s + sy) + x * s) * 4
                for sx in range(s):
                    ar = px[base + sx * 4 + 3] / 255.0
                    r += px[base + sx * 4] * ar
                    g += px[base + sx * 4 + 1] * ar
                    b += px[base + sx * 4 + 2] * ar
                    a += px[base + sx * 4 + 3]
            alpha_sum = max(a / 255.0, 1e-6)
            row += bytes((
                min(255, int(b / alpha_sum)),
                min(255, int(g / alpha_sum)),
                min(255, int(r / alpha_sum)),
                int(a / (s * s)),
            ))
        rows.append(bytes(row))

    # CUR payload：BITMAPINFOHEADER(高=2h) + 自底向上 XOR + AND 掩码
    xor = b"".join(reversed(rows))
    and_stride = ((SIZE + 31) // 32) * 4
    and_mask = b"\x00" * (and_stride * SIZE)  # 32bpp + alpha，掩码全 0 即可
    ih = struct.pack("<IiiHHIIiiII", 40, SIZE, SIZE * 2, 1, 32, 0, len(xor) + len(and_mask), 0, 0, 0, 0)
    return ih + xor + and_mask


def cur(payload, hot):
    head = struct.pack("<HHH", 0, 2, 1)  # type=2 (cursor)
    entry = struct.pack("<BBBBHHII", SIZE, SIZE, 0, 0, hot[0], hot[1], len(payload), 22)
    return head + entry + payload


if __name__ == "__main__":
    kind = "pen"
    args = [a for a in sys.argv[1:]]
    if args and args[0] in ("pen", "eraser"):
        kind = args.pop(0)
    out = args[0] if args else f"src/ParsecDraw/Assets/{kind}.cur"
    import os
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    data = cur(render(kind), PEN_HOT if kind == "pen" else ERASER_HOT)
    with open(out, "wb") as f:
        f.write(data)
    print(f"wrote {out} ({len(data)} bytes)")
