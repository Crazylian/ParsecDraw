# -*- coding: utf-8 -*-
"""ParsecDraw 应用图标生成器（零第三方依赖）。

用法:  python tools/make_icon.py [输出.ico]
设计:  深靛蓝圆角方块（与 UI 配色一致）+ 珊瑚红渐细笔锋斜掠 + 白色笔尖高光。
实现:  圆角矩形用解析 SDF 抗锯齿；笔锋沿二次贝塞尔冲压圆盘，4x 超采样；
       PNG 由 zlib+struct 手写，ICO 为 Vista+ PNG-in-ICO 容器（16/32/48/256）。
"""
import math
import struct
import sys
import zlib

# ---- 配色（与 Ui/Styles.xaml 保持一致）----
BG_TOP = (0x30, 0x30, 0x4E)
BG_BOT = (0x19, 0x19, 0x28)
STROKE_A = (0xFF, 0x6B, 0x77)   # 笔锋亮端
STROKE_B = (0xE8, 0x2D, 0x43)   # 笔锋暗端（沿曲线渐变）
TIP = (0xFF, 0xFF, 0xFF)


def lerp(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def sd_rounded_rect(x, y, cx, cy, hw, hh, r):
    """点到圆角矩形的符号距离（内负外正）。"""
    qx = abs(x - cx) - (hw - r)
    qy = abs(y - cy) - (hh - r)
    ox = max(qx, 0.0)
    oy = max(qy, 0.0)
    return math.hypot(ox, oy) + min(max(qx, qy), 0.0) - r


def bez(p0, p1, p2, t):
    u = 1.0 - t
    return (u * u * p0[0] + 2 * u * t * p1[0] + t * t * p2[0],
            u * u * p0[1] + 2 * u * t * p1[1] + t * t * p2[1])


def bez_tangent(p0, p1, p2, t):
    u = 1.0 - t
    return (2 * (u * (p1[0] - p0[0]) + t * (p2[0] - p1[0])),
            2 * (u * (p1[1] - p0[1]) + t * (p2[1] - p1[1])))


def render(size):
    """渲染 RGBA 像素矩阵（size x size，坐标单位=逻辑像素）。"""
    s = 4  # 超采样倍数
    n = size * s
    # 几何一律在超采样空间计算（比例同 256 逻辑坐标系）
    k2 = n / 256.0
    cx = cy = n / 2.0
    hw = hh = n / 2.0 - 16 * k2            # 四周留 16px 边
    radius = 56 * k2
    # 笔锋曲线：左下起笔 → 控制点偏上 → 右上收笔（逻辑坐标，冲压时再乘 s）
    k = size / 256.0
    p0, p1, p2 = (74 * k, 184 * k), (88 * k, 52 * k), (188 * k, 92 * k)
    w_head, w_tail = 13 * k, 5 * k          # 半宽（起笔粗、收笔细）

    px = bytearray(n * n * 4)               # 预乘前普通 RGBA
    # -- 背景圆角方块（SDF，逐像素）--
    for y in range(n):
        fy = y + 0.5
        row = n * y * 4
        for x in range(n):
            fx = x + 0.5
            d = sd_rounded_rect(fx, fy, cx, cy, hw, hh, radius)
            a = 1.0 if d <= -0.5 else (0.0 if d >= 0.5 else 0.5 - d)
            if a <= 0:
                continue
            c = lerp(BG_TOP, BG_BOT, (fy / n) ** 1.2)
            i = row + x * 4
            px[i] = int(c[0] * a)
            px[i + 1] = int(c[1] * a)
            px[i + 2] = int(c[2] * a)
            px[i + 3] = int(255 * a)

    # -- 笔锋：沿贝塞尔冲压圆盘（每盘按扫描线精确跨度，硬冲压靠超采样抗锯齿）--
    steps = 220
    for i in range(steps + 1):
        t = i / steps
        x, y = bez(p0, p1, p2, t)
        tx, ty = bez_tangent(p0, p1, p2, t)
        tl = math.hypot(tx, ty) or 1.0
        # 单位法线
        nxv, nyv = -ty / tl, tx / tl
        # 半宽沿曲线先增后减（起笔粗→收笔细）
        hwid = (w_head + (w_tail - w_head) * (t ** 0.7)) * s
        r = hwid
        cxs, cys = x * s, y * s
        col = lerp(STROKE_A, STROKE_B, t)
        y0 = max(0, int(cys - r))
        y1 = min(n - 1, int(cys + r) + 1)
        for yy in range(y0, y1):
            dy = yy + 0.5 - cys
            dd = r * r - dy * dy
            if dd <= 0:
                continue
            half = math.sqrt(dd)
            x0 = max(0, int(cxs - half))
            x1 = min(n - 1, int(cxs + half))
            row = n * yy * 4
            for xx in range(x0, x1 + 1):
                j = row + xx * 4
                if px[j + 3] == 0:
                    continue  # 方块外不画
                px[j] = int(col[0])
                px[j + 1] = int(col[1])
                px[j + 2] = int(col[2])
                px[j + 3] = 255

    # -- 笔尖高光：起笔处白色小圆 --
    tipr = 6.5 * k * s
    tipx, tipy = p0[0] * s - 2 * k * s, p0[1] * s + 3 * k * s
    for yy in range(max(0, int(tipy - tipr)), min(n, int(tipy + tipr) + 1)):
        dy = yy + 0.5 - tipy
        dd = tipr * tipr - dy * dy
        if dd <= 0:
            continue
        half = math.sqrt(dd)
        for xx in range(max(0, int(tipx - half)), min(n - 1, int(tipx + half)) + 1):
            j = (n * yy + xx) * 4
            if px[j + 3] == 0:
                continue
            px[j] = px[j + 1] = px[j + 2] = 255
            px[j + 3] = 255

    # -- 4x 盒式降采样 --
    out = bytearray(size * size * 4)
    for y in range(size):
        for x in range(size):
            r = g = b = a = 0
            for sy in range(s):
                row = (n * (y * s + sy) + x * s) * 4
                for sx_ in range(s):
                    j = row + sx_ * 4
                    ar = px[j + 3] / 255.0
                    r += px[j] * ar
                    g += px[j + 1] * ar
                    b += px[j + 2] * ar
                    a += px[j + 3]
            i = (size * y + x) * 4
            aa = a / (s * s)
            out[i + 3] = int(aa)
            if aa > 0:
                # 通道值按预权和解除预乘：sum(c*ar) / sum(ar)
                alpha_sum = max(a / 255.0, 1e-6)
                out[i] = min(255, int(r / alpha_sum))
                out[i + 1] = min(255, int(g / alpha_sum))
                out[i + 2] = min(255, int(b / alpha_sum))
    return bytes(out)


def png(w, h, rgba):
    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def ico(sizes):
    blobs = [(sz, png(sz, sz, render(sz))) for sz in sizes]
    head = struct.pack("<HHH", 0, 1, len(blobs))
    entries = b""
    off = 6 + 16 * len(blobs)
    for sz, blob in blobs:
        dim = 0 if sz >= 256 else sz
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), off)
        off += len(blob)
    return head + entries + b"".join(b for _, b in blobs)


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "src/ParsecDraw/Assets/app.ico"
    import os
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    data = ico([16, 32, 48, 256])
    with open(out, "wb") as f:
        f.write(data)
    print(f"wrote {out} ({len(data)} bytes)")
