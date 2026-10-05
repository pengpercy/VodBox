"""Export platform icon formats from the approved transparent tile (requires Pillow)."""
from pathlib import Path
import argparse
from PIL import Image, ImageDraw, ImageChops

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "src/VodBox.Desktop/Assets/Icons"
SIZES = (16, 24, 32, 48, 64, 128, 256, 512, 1024)

def generate(source):
    OUT.mkdir(parents=True, exist_ok=True)
    with Image.open(source) as original:
        image = original.convert("RGBA")
    bounds = image.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError("Icon is empty")
    if image.getchannel("A").getextrema()[0] != 0:
        raise ValueError("Input must have a transparent background")
    # Clean the extraction's stray fringe using the rounded-square tile bounds.
    # Coordinates refer to the committed 1254px extraction; artwork pixels are untouched.
    tile = image.crop((71, 71, 1183, 1183))
    mask = Image.new("L", (tile.width * 4, tile.height * 4))
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, mask.width - 1, mask.height - 1), radius=240 * 4, fill=255)
    mask = mask.resize(tile.size, Image.Resampling.LANCZOS)
    tile.putalpha(ImageChops.multiply(tile.getchannel("A"), mask))
    side = max(tile.size)
    square = Image.new("RGBA", (side, side))
    square.alpha_composite(tile, ((side-tile.width)//2, (side-tile.height)//2))
    master = square.resize((1024, 1024), Image.Resampling.LANCZOS)
    master.save(OUT / "vodbox.png")
    master.save(OUT / "vodbox.ico", sizes=[(s, s) for s in SIZES if s <= 256])
    for size in SIZES:
        target = OUT / "linux" / str(size)
        target.mkdir(parents=True, exist_ok=True)
        master.resize((size, size), Image.Resampling.LANCZOS).save(target / "vodbox.png")
    # Legacy ICNS consumes a complete canvas: reserve 100 transparent pixels per side.
    mac = Image.new("RGBA", (1024, 1024))
    mac.alpha_composite(master.resize((824, 824), Image.Resampling.LANCZOS), (100, 100))
    mac.save(OUT / "vodbox-macos.png")
    mac.save(OUT / "vodbox.icns", sizes=[(s, s) for s in (16, 32, 64, 128, 256, 512, 1024)])
    print("Exported Windows ICO, macOS ICNS (824px artwork / 1024px canvas), Linux PNG sizes.")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, nargs="?", default=OUT / "Source/tile-extracted.png")
    generate(parser.parse_args().source)
