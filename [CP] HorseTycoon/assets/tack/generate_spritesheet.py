from PIL import Image
import os

ICON_SIZE   = 16
FRAME_SIZE  = 32
SADDLE_CROP = (0, 64, 32, 96)   # frame 15 of horse animation (down-facing saddle)
BRIDLE_CROP = (64, 32, 96, 64)  # frame 10 of horse animation (down-facing bridle)

TACK_DIR    = "[CP] HorseTycoon/assets/tack"
OVERLAY_DIR = "HorseTycoon/assets/horse_overlays"

variants = ["Ace","Aurora","Bisexual","Black","Brown","Candy","Ember","Gold","Green",
            "Ice","Lavender","Lesbian","Meadow","Mint","Navy","NonBinary","Ocean","Orange","Pink",
            "Peach","Plum","Rainbow","Red","Sky","Sunset","Teal","Trans","White"]

N    = len(variants)
CELL = FRAME_SIZE  # 32 wide for all columns; icons centered within
TOTAL_W = N * CELL
TOTAL_H = ICON_SIZE + FRAME_SIZE + FRAME_SIZE  # 80

sheet = Image.new("RGBA", (TOTAL_W, TOTAL_H), (0, 0, 0, 0))

for i, var in enumerate(variants):
    cell_x = i * CELL

    # Row 0: icon 16x16, centred in 32-wide cell
    try:
        img = Image.open(os.path.join(TACK_DIR, f"Saddle{var}.png")).convert("RGBA")
        offset = (CELL - ICON_SIZE) // 2
        sheet.paste(img, (cell_x + offset, 0), img)
    except Exception as e:
        print(f"WARN icon {var}: {e}")

    # Row 1: saddle down 32x32, native size
    try:
        overlay = Image.open(os.path.join(OVERLAY_DIR, f"Saddle_{var}.png")).convert("RGBA")
        frame = overlay.crop(SADDLE_CROP)
        sheet.paste(frame, (cell_x, ICON_SIZE), frame)
    except Exception as e:
        print(f"WARN saddle {var}: {e}")

    # Row 2: bridle down 32x32, native size
    try:
        overlay = Image.open(os.path.join(OVERLAY_DIR, f"Bridle_{var}.png")).convert("RGBA")
        frame = overlay.crop(BRIDLE_CROP)
        sheet.paste(frame, (cell_x, ICON_SIZE + FRAME_SIZE), frame)
    except Exception as e:
        print(f"WARN bridle {var}: {e}")

out = "[CP] HorseTycoon/assets/tack/TackSpriteSheet.png"
sheet.save(out)
print(f"Saved {TOTAL_W}x{TOTAL_H} → {out}")
