from PIL import Image

OVERLAY_DIR = '/Users/kemurray/StardewValleyKristinMods/HorseTycoon/HorseTycoon/assets/horse_overlays'
CP_ASSETS   = '/Users/kemurray/StardewValleyKristinMods/HorseTycoon/[CP] HorseTycoon/assets/tack'

COLORS = {
    'White':    (255, 255, 255),
    'Black':    ( 42,  33,  26),
    'Red':      (185,  48,  38),
    'Orange':   (255, 140,   0),
    'Teal':     (  0, 160, 150),
    'Lavender': (180, 130, 215),
    'Green':    ( 62, 142,  66),
    'Navy':     ( 30,  50, 110),
    'Pink':     (240, 130, 180),
    'Gold':     (210, 160,  40),
    'Peach':    (255, 175, 130),
    'Plum':     ( 90,  40, 120),
    'Sky':      (130, 200, 245),
    'Mint':     (145, 235, 195),
}

# Three-stop vertical gradients: colour runs top -> middle -> bottom of each frame.
GRADIENTS = {
    'Sunset': [(255, 205,  90), (240, 110,  70), (105,  50, 130)],   # gold -> coral -> dusk purple
    'Ocean':  [(150, 235, 235), ( 40, 150, 205), ( 20,  45, 110)],   # foam -> sea -> deep water
    'Aurora': [(120, 240, 170), ( 60, 190, 220), (150,  90, 220)],   # green -> cyan -> violet
    'Meadow': [(235, 230, 120), (120, 200,  90), ( 30, 105,  70)],   # sun -> grass -> deep green
    'Candy':  [(255, 165, 205), (198, 140, 242), (100, 180, 255)],   # pink -> orchid -> sky
    'Ember':  [(255, 235, 120), (250, 140,  30), (150,  20,  25)],   # flame yellow -> orange -> ember red
}

# Bias the gradient's position for a given ramp: <1 reaches the later stops sooner, so the last
# colour covers more of the sprite. Candy's sky blue was barely visible on an even ramp because
# the saddle's silhouette is widest at the top. Default is 1.0 (evenly spaced).
GRADIENT_BIAS = {
    'Candy': 0.6,
}

# Frame grid of the horse-overlay sheets; the icons are a single 16x16 cell.
FRAME_SIZE = 32

RAINBOW_COLORS = [
    (255,  50,  50),  # Red
    (255, 160,   0),  # Orange
    (255, 240,   0),  # Yellow
    ( 80, 220,  80),  # Green
    ( 50, 120, 255),  # Blue
    (180,  80, 220),  # Violet
]
RAINBOW_STRIPE_WIDTH = 2


def _max_lum(img, px):
    m = 0.0
    for y in range(img.height):
        for x in range(img.width):
            r, g, b, a = px[x, y]
            if a > 0:
                lum = (0.299*r + 0.587*g + 0.114*b) / 255.0
                if lum > m:
                    m = lum
    return m


def recolor(src_path, out_path, target):
    img = Image.open(src_path).convert('RGBA')
    px = img.load()
    max_lum = _max_lum(img, px)
    out = Image.new('RGBA', img.size, (0, 0, 0, 0))
    op = out.load()
    tr, tg, tb = target
    for y in range(img.height):
        for x in range(img.width):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            lum_n = (0.299*r + 0.587*g + 0.114*b) / 255.0 / max_lum if max_lum > 0 else 0.0
            op[x, y] = (min(255, int(tr*lum_n)), min(255, int(tg*lum_n)), min(255, int(tb*lum_n)), a)
    out.save(out_path)
    print(f'  -> {out_path}')


def recolor_rainbow(src_path, out_path):
    img = Image.open(src_path).convert('RGBA')
    px = img.load()
    max_lum = _max_lum(img, px)
    out = Image.new('RGBA', img.size, (0, 0, 0, 0))
    op = out.load()
    n = len(RAINBOW_COLORS)
    for y in range(img.height):
        for x in range(img.width):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            lum_n = (0.299*r + 0.587*g + 0.114*b) / 255.0 / max_lum if max_lum > 0 else 0.0
            tr, tg, tb = RAINBOW_COLORS[(x + y) // RAINBOW_STRIPE_WIDTH % n]
            op[x, y] = (min(255, int(tr*lum_n)), min(255, int(tg*lum_n)), min(255, int(tb*lum_n)), a)
    out.save(out_path)
    print(f'  -> {out_path}')


def _gradient_at(stops, t):
    """Colour at position t (0..1) along an evenly-spaced list of colour stops."""
    t = min(max(t, 0.0), 1.0)
    span = t * (len(stops) - 1)
    i = min(int(span), len(stops) - 2)
    f = span - i
    a, b = stops[i], stops[i + 1]
    return tuple(a[c] + (b[c] - a[c]) * f for c in range(3))


def recolor_gradient(src_path, out_path, stops, bias=1.0):
    """Vertical 3-stop gradient, computed per animation frame so every frame of the sheet
    shows the whole gradient rather than a slice of one that spans the sheet."""
    img = Image.open(src_path).convert('RGBA')
    px = img.load()
    max_lum = _max_lum(img, px)
    out = Image.new('RGBA', img.size, (0, 0, 0, 0))
    op = out.load()
    cell = FRAME_SIZE if img.width >= FRAME_SIZE * 2 else img.height

    for cy in range(0, img.height, cell):
        for cx in range(0, img.width, cell):
            # The tack covers only part of its frame, so stretch the gradient over the
            # rows that actually have pixels in this cell.
            rows = [y for y in range(cy, min(cy + cell, img.height))
                    if any(px[x, y][3] > 0 for x in range(cx, min(cx + cell, img.width)))]
            if not rows:
                continue
            y0, y1 = rows[0], rows[-1]
            for y in range(y0, y1 + 1):
                t = ((y - y0) / (y1 - y0)) ** bias if y1 > y0 else 0.0
                tr, tg, tb = _gradient_at(stops, t)
                for x in range(cx, min(cx + cell, img.width)):
                    r, g, b, a = px[x, y]
                    if a == 0:
                        continue
                    lum_n = (0.299*r + 0.587*g + 0.114*b) / 255.0 / max_lum if max_lum > 0 else 0.0
                    op[x, y] = (min(255, int(tr*lum_n)), min(255, int(tg*lum_n)),
                                min(255, int(tb*lum_n)), a)
    out.save(out_path)
    print(f'  -> {out_path}')


for name, rgb in COLORS.items():
    print(f'{name} {rgb}')
    recolor(f'{OVERLAY_DIR}/Saddle_Brown.png', f'{OVERLAY_DIR}/Saddle_{name}.png', rgb)
    recolor(f'{OVERLAY_DIR}/Bridle_Brown.png', f'{OVERLAY_DIR}/Bridle_{name}.png', rgb)
    recolor(f'{CP_ASSETS}/SaddleBrown.png',    f'{CP_ASSETS}/Saddle{name}.png',    rgb)

PRIDE_FLAGS = {
    'Trans': [
        ( 91, 206, 250),  # Blue
        (245, 169, 184),  # Pink
        (255, 255, 255),  # White
        (245, 169, 184),  # Pink
        ( 91, 206, 250),  # Blue
    ],
    'Lesbian': [
        (213,  45,   0),  # Dark orange-red
        (255, 154,  86),  # Orange
        (255, 255, 255),  # White
        (211,  98, 164),  # Pink
        (163,   2,  98),  # Dark rose
    ],
    'Ace': [
        (  0,   0,   0),  # Black
        (164, 164, 164),  # Grey
        (255, 255, 255),  # White
        (128,   0, 128),  # Purple
    ],
    'NonBinary': [
        (252, 244,  52),  # Yellow
        (255, 255, 255),  # White
        (156,  89, 209),  # Purple
        (  0,   0,   0),  # Black
    ],
    'Bisexual': [
        (214,   2, 112),  # Pink
        (155,  79, 150),  # Purple
        (  0,  56, 168),  # Blue
    ],
}

def recolor_stripes(src_path, out_path, colors):
    img = Image.open(src_path).convert('RGBA')
    px = img.load()
    max_lum = _max_lum(img, px)
    out = Image.new('RGBA', img.size, (0, 0, 0, 0))
    op = out.load()
    n = len(colors)
    for y in range(img.height):
        for x in range(img.width):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            lum_n = (0.299*r + 0.587*g + 0.114*b) / 255.0 / max_lum if max_lum > 0 else 0.0
            tr, tg, tb = colors[(x + y) // RAINBOW_STRIPE_WIDTH % n]
            op[x, y] = (min(255, int(tr*lum_n)), min(255, int(tg*lum_n)), min(255, int(tb*lum_n)), a)
    out.save(out_path)
    print(f'  -> {out_path}')


for name, stops in GRADIENTS.items():
    bias = GRADIENT_BIAS.get(name, 1.0)
    print(f'{name} gradient {stops} bias={bias}')
    recolor_gradient(f'{OVERLAY_DIR}/Saddle_Brown.png', f'{OVERLAY_DIR}/Saddle_{name}.png', stops, bias)
    recolor_gradient(f'{OVERLAY_DIR}/Bridle_Brown.png', f'{OVERLAY_DIR}/Bridle_{name}.png', stops, bias)
    recolor_gradient(f'{CP_ASSETS}/SaddleBrown.png',    f'{CP_ASSETS}/Saddle{name}.png',    stops, bias)

print('Rainbow (2px diagonal stripes)')
recolor_rainbow(f'{OVERLAY_DIR}/Saddle_Brown.png', f'{OVERLAY_DIR}/Saddle_Rainbow.png')
recolor_rainbow(f'{OVERLAY_DIR}/Bridle_Brown.png', f'{OVERLAY_DIR}/Bridle_Rainbow.png')
recolor_rainbow(f'{CP_ASSETS}/SaddleBrown.png',    f'{CP_ASSETS}/SaddleRainbow.png')

for name, colors in PRIDE_FLAGS.items():
    print(f'{name} pride flag')
    recolor_stripes(f'{OVERLAY_DIR}/Saddle_Brown.png', f'{OVERLAY_DIR}/Saddle_{name}.png', colors)
    recolor_stripes(f'{OVERLAY_DIR}/Bridle_Brown.png', f'{OVERLAY_DIR}/Bridle_{name}.png', colors)
    recolor_stripes(f'{CP_ASSETS}/SaddleBrown.png',    f'{CP_ASSETS}/Saddle{name}.png',    colors)

print('Done.')
