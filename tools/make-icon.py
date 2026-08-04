"""
Render the TrampList mark to a multi-resolution Windows .ico.

Drawn directly rather than rasterising the SVG so there is no dependency on a
converter: the shapes are a filled crest plus three strokes, all expressible with
PIL primitives at 4x supersampling for clean edges.

Geometry is the site's static/icon.svg, in its 64-unit coordinate space.
"""
from PIL import Image, ImageDraw

RUST = (222, 97, 41, 255)   # #de6129 — the theme's dark-mode primary
LEG = (0, 0, 0, 255)        # black: holds contrast against rust at small sizes
SS = 4                      # supersample factor

# Windows uses these slots: 16 and 32 for lists and the title bar, 256 for the
# "extra large icons" view and the taskbar on high-DPI displays.
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


def render(size: int) -> Image.Image:
    s = size * SS
    k = s / 64.0  # scale from the SVG's 64-unit space
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Crest: M6 3 H58 V40.6 L32 61 L6 40.6 Z
    d.polygon(
        [(6 * k, 3 * k), (58 * k, 3 * k), (58 * k, 40.6 * k),
         (32 * k, 61 * k), (6 * k, 40.6 * k)],
        fill=RUST,
    )

    # Leg, stroke-width 5 in SVG units. Thinned at icon sizes: a 5-unit stroke is
    # ~8% of the tile, which at 16px leaves the leg fatter than the rust it sits on
    # and the mark reads as a black blob. Larger slots keep the SVG's weight.
    stroke = 5.0 if size >= 48 else 3.9
    w = max(1, round(stroke * k))
    for a, b in (((39, 12), (27, 25)),   # thigh
                 ((27, 25), (33, 37)),   # shin
                 ((23, 41), (41, 41))):  # foot
        d.line([(a[0] * k, a[1] * k), (b[0] * k, b[1] * k)], fill=LEG, width=w)
        # PIL doesn't join line segments, so dab the joints closed.
        for p in (a, b):
            r = w / 2
            d.ellipse([p[0] * k - r, p[1] * k - r, p[0] * k + r, p[1] * k + r], fill=LEG)

    return img.resize((size, size), Image.LANCZOS)


frames = [render(n) for n in SIZES]
out = r"F:/WIN_DATA/Documents/Coding/TrampListManager/src/TrampListManager/app.ico"
frames[-1].save(out, format="ICO", sizes=[(n, n) for n in SIZES])
print(f"wrote {out}")

img = Image.open(out)
print("ico sizes:", sorted(img.info.get("sizes", [])))
