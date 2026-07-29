"""Generate 16x16 IV potion bottle sprites (Speed/Sprint/Jump) for [CP] HorseTycoon."""
from PIL import Image

OUT = '/Users/kemurray/StardewValleyKristinMods/HorseTycoon/[CP] HorseTycoon/assets/potions'

POTIONS = {
    'IVPotionSpeed':  ((70, 130, 235), (140, 190, 255)),   # blue liquid, highlight
    'IVPotionSprint': ((225, 95, 45),  (255, 170, 110)),   # orange
    'IVPotionJump':   ((80, 185, 90),  (160, 235, 160)),   # green
    'TrainingPotion': ((160, 80, 210), (215, 165, 250)),   # purple
    'GallopPotion':   ((235, 175, 45), (255, 225, 140)),   # amber
    'CoatPotion':     ((225, 85, 155), (255, 170, 215)),   # pink
}

GLASS = (210, 230, 240, 255)
GLASS_DARK = (150, 175, 195, 255)
CORK = (150, 105, 60, 255)
CORK_DARK = (110, 75, 40, 255)
OUTLINE = (45, 45, 60, 255)

def make(name, liquid, hi):
    img = Image.new('RGBA', (16, 16), (0, 0, 0, 0))
    px = img.load()

    def put(x, y, c):
        px[x, y] = c if len(c) == 4 else (*c, 255)

    # Cork (rows 1-3, cols 7-8)
    for y in (1, 2, 3):
        for x in (7, 8):
            put(x, y, CORK if x == 7 else CORK_DARK)
    put(6, 1, OUTLINE); put(9, 1, OUTLINE)
    put(6, 2, OUTLINE); put(9, 2, OUTLINE)
    put(6, 3, OUTLINE); put(9, 3, OUTLINE)

    # Neck (rows 4-6, cols 6-9): glass walls, empty inside
    for y in (4, 5, 6):
        put(5, y, OUTLINE)
        put(6, y, GLASS)
        put(7, y, (255, 255, 255, 70))
        put(8, y, (255, 255, 255, 70))
        put(9, y, GLASS_DARK)
        put(10, y, OUTLINE)

    # Bulb (rows 7-14): widening flask filled with liquid from row 8 down
    widths = {7: (4, 11), 8: (3, 12), 9: (2, 13), 10: (2, 13), 11: (2, 13), 12: (2, 13), 13: (3, 12), 14: (4, 11)}
    for y, (x0, x1) in widths.items():
        put(x0, y, OUTLINE)
        put(x1, y, OUTLINE)
        for x in range(x0 + 1, x1):
            if y == 7:
                put(x, y, GLASS if x < 8 else GLASS_DARK)
            else:
                put(x, y, liquid)
    # bottom outline
    for x in range(5, 11):
        put(x, 15, OUTLINE)
    put(4, 14, OUTLINE); put(11, 14, OUTLINE)

    # Liquid shading: highlight upper-left, dark lower-right
    dark = tuple(max(0, c - 60) for c in liquid)
    for y in range(9, 12):
        put(4, y, hi)
    put(5, 9, hi)
    for y in range(12, 15):
        for x in range(9, widths.get(y, (0, 0))[1]):
            put(x, y, dark)
    # sparkle
    put(6, 10, (255, 255, 255, 220))

    img.save(f'{OUT}/{name}.png')
    print('wrote', name)

for name, (liquid, hi) in POTIONS.items():
    make(name, liquid, hi)
