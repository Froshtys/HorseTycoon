"""Rebuild the tack cells in the festival map tilesheet (HorseTycoonTileSet.png).

Each tack colour occupies a 2x2 cell laid out as:

    (row R,   col C  ) = shop item icon, nudged down 2px
    (row R,   col C+1) = saddle, cropped from the down-facing horse overlay frame
    (row R+1, col C  ) = bridle, cropped from the right-facing horse overlay frame
    (row R+1, col C+1) = unused

The crops below were reverse-engineered from the hand-authored original cells and
reproduce 13 of the 14 exactly. (Ace's saddle differs: the sheet holds a copy from
before its palette was retuned, so re-running this refreshes it.)

Run from the repo root:  python3 tools/build_tack_tiles.py
"""
from PIL import Image

SHEET = '[CP] HorseTycoon/assets/maps/HorseTycoonTileSet.png'
TACK_DIR = '[CP] HorseTycoon/assets/tack'
OVERLAY_DIR = 'HorseTycoon/assets/horse_overlays'

ICON_Y = 2                          # icon sits 2px down inside its tile
SADDLE_CROP = (9, 70, 25, 86)       # 16x16 out of the down-facing saddle frame
BRIDLE_CROP = (80, 35, 96, 51)      # 16x16 out of the right-facing bridle frame

# Row pair -> colours, in the order they sit on the sheet. Rows 0-1 are the original
# fourteen; rows 5-6 were added when the remaining colours arrived. Both runs are
# alphabetical within themselves. Appending a new colour means extending ROWS[5].
ROWS = {
    0: ['Ace', 'Bisexual', 'Black', 'Brown', 'Ice', 'Lavender', 'Lesbian',
        'NonBinary', 'Orange', 'Rainbow', 'Red', 'Teal', 'Trans', 'White'],
    5: ['Aurora', 'Candy', 'Gold', 'Green', 'Meadow', 'Navy', 'Ocean',
        'Peach', 'Pink', 'Plum', 'Sunset'],
}

COLS = 28


def build(sheet, row, colours):
    for k, v in enumerate(colours):
        col = 2 * k
        if col + 1 >= COLS:
            raise SystemExit(f'row {row} overflows: {v} needs col {col + 1}')
        icon = Image.open(f'{TACK_DIR}/Saddle{v}.png').convert('RGBA')
        saddle = Image.open(f'{OVERLAY_DIR}/Saddle_{v}.png').convert('RGBA').crop(SADDLE_CROP)
        bridle = Image.open(f'{OVERLAY_DIR}/Bridle_{v}.png').convert('RGBA').crop(BRIDLE_CROP)
        sheet.paste(icon, (col * 16, row * 16 + ICON_Y), icon)
        sheet.paste(saddle, ((col + 1) * 16, row * 16), saddle)
        sheet.paste(bridle, (col * 16, (row + 1) * 16), bridle)


if __name__ == '__main__':
    sheet = Image.open(SHEET).convert('RGBA')
    for row, colours in ROWS.items():
        build(sheet, row, colours)
    sheet.save(SHEET)
    total = sum(len(v) for v in ROWS.values())
    print(f'{total} tack cells written to {SHEET}')
