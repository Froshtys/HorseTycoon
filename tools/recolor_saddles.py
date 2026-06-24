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
}

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
