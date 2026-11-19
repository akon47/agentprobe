"""Draws the agentprobe icon and writes src/AgentProbe/agentprobe.ico + docs/icon.png.

The mark: a probe ring with a signal trace running through it - "reach into a machine and read its pulse".
Run:  python tools/make-icon.py   (needs Pillow)
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
NAVY_TOP = (17, 24, 39)
NAVY_BOTTOM = (30, 41, 82)
CYAN = (34, 211, 238)
WHITE = (255, 255, 255)


def draw(size: int) -> Image.Image:
    """Render at 8x and downsample, so edges stay smooth at every size."""
    scale = 8
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Rounded-square background with a vertical gradient.
    gradient = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(gradient)
    for y in range(s):
        t = y / (s - 1)
        gd.line([(0, y), (s, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(NAVY_TOP, NAVY_BOTTOM)) + (255,))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=round(s * 0.22), fill=255)
    img.paste(gradient, (0, 0), mask)

    d = ImageDraw.Draw(img)
    cx, cy = s * 0.5, s * 0.5
    small = size <= 24

    # Probe ring.
    r = s * (0.30 if small else 0.29)
    w = round(s * (0.085 if small else 0.062))
    ring = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    rd = ImageDraw.Draw(ring)
    rd.ellipse([cx - r, cy - r, cx + r, cy + r], outline=WHITE, width=w)

    if small:
        # Too few pixels for a trace: a solid dot reads as "probe point".
        img.alpha_composite(ring)
        dot = s * 0.11
        d.ellipse([cx - dot, cy - dot, cx + dot, cy + dot], fill=CYAN)
    else:
        # Signal trace through the ring, with a clear gap where it crosses the ring line.
        trace = [(0.10, 0.50), (0.36, 0.50), (0.43, 0.33), (0.52, 0.68), (0.59, 0.44), (0.64, 0.50), (0.90, 0.50)]
        pts = [(x * s, y * s) for x, y in trace]
        gap = round(s * 0.115)
        rd.line(pts, fill=(0, 0, 0, 0), width=gap, joint="curve")   # cut the ring open where the trace crosses it
        img.alpha_composite(ring)
        d.line(pts, fill=CYAN, width=round(s * 0.058), joint="curve")
        cap = s * 0.029
        for x, y in (pts[0], pts[-1]):
            d.ellipse([x - cap, y - cap, x + cap, y + cap], fill=CYAN)

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [draw(n) for n in sizes]
    ico = ROOT / "src" / "AgentProbe" / "agentprobe.ico"
    images[-1].save(ico, format="ICO", sizes=[(n, n) for n in sizes], append_images=images[:-1])
    (ROOT / "docs").mkdir(exist_ok=True)
    draw(256).save(ROOT / "docs" / "icon.png")
    print("wrote", ico, "and docs/icon.png")


if __name__ == "__main__":
    main()
