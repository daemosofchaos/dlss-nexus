"""Build Windows icon resources from the generated master image. Build-time only.

Requires Pillow; it is not a dependency of the application or installer.
"""
from pathlib import Path
import json
from PIL import Image

brand = Path(__file__).resolve().parents[1] / "Assets" / "Brand"
source = Image.open(brand / "nexus-icon-master.png").convert("RGBA")
assert source.size == (1024, 1024), source.size
assert source.getextrema()[3][0] == 0, "Master must have a transparent background"
sizes = [(n, n) for n in (16, 24, 32, 48, 64, 128, 256)]
source.save(brand / "nexus.ico", sizes=sizes, bitmap_format="png")
source.resize((256, 256), Image.Resampling.LANCZOS).save(brand / "nexus-mark.png")
icon = Image.open(brand / "nexus.ico")
assert icon.ico.sizes() == set(sizes), icon.ico.sizes()
for size in sizes:
    frame = icon.ico.getimage(size)
    assert frame.size == size
    assert frame.convert("RGBA").getextrema()[3][0] == 0
print(json.dumps({"master": str(brand / "nexus-icon-master.png"),
                  "mode": source.mode, "alphaRange": source.getextrema()[3],
                  "icoFrames": sorted(icon.ico.sizes()), "valid": True}, indent=2))