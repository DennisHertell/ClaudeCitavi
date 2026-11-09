"""Draws numbered markers on the screenshots of the macro guide (raw/ -> img/).

Usage: py -3.12 docs/anleitung-makro/annotate.py
Each entry: list of (kind, box, label). kind "box" = red frame with number, "blur" = hide content.
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = Path(__file__).parent
RED = (214, 39, 40)
FONT = ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", 18)

MARKS = {
    "00_paket_ordner.png": [("blur", (300, 50, 362, 78), None), ("blur", (34, 208, 300, 236), None),
                            ("blur", (300, 536, 1000, 560), None), ("box", (328, 253, 565, 275), "1")],
    "01_startfenster.png": [("blur", (52, 282, 515, 552), None), ("box", (50, 244, 300, 271), "2")],
    "02_extras_menu.png": [("crop", (0, 0, 1118, 720), None), ("box", (290, 32, 330, 53), "3"), ("box", (311, 177, 560, 200), "4")],
    "04_datei_menu.png": [("box", (9, 32, 50, 53), "5"), ("box", (11, 55, 203, 77), "6")],
    "05_oeffnen_dialog.png": [("blur", (248, 44, 302, 70), None), ("blur", (34, 188, 322, 216), None),
                              ("box", (202, 480, 716, 504), "7"), ("box", (725, 508, 819, 536), "8")],
    "06_makro_geladen.png": [("box", (142, 57, 230, 81), "9")],
    "07_meldung.png": [("box", (12, 44, 258, 76), None), ("box", (186, 104, 254, 131), "10")],
}


def badge(draw: ImageDraw.ImageDraw, x: int, y: int, label: str) -> None:
    r = 14 if len(label) < 2 else 17
    draw.ellipse((x - r, y - r, x + r, y + r), fill=RED, outline="white", width=2)
    w = draw.textlength(label, font=FONT)
    draw.text((x - w / 2, y - 12), label, font=FONT, fill="white")


def main() -> None:
    out = HERE / "img"
    out.mkdir(exist_ok=True)
    for name, marks in MARKS.items():
        img = Image.open(HERE / "raw" / name).convert("RGB")
        for kind, box, _ in marks:
            if kind == "crop":
                img = img.crop(box)
            elif kind == "blur":
                region = img.crop(box).filter(ImageFilter.GaussianBlur(9))
                img.paste(region, box[:2])
        draw = ImageDraw.Draw(img)
        for kind, box, label in marks:
            if kind != "box":
                continue
            draw.rounded_rectangle(box, radius=4, outline=RED, width=3)
            if label:
                # Badge above the top left corner, so it never covers the marked control.
                badge(draw, max(17, box[0] + 4), max(17, box[1] - 16), label)
        img.save(out / name, optimize=True)
        print("ok", name, img.size)


if __name__ == "__main__":
    main()
