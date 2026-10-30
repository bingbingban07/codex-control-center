"""Draw the Control Center icon from geometric primitives; no generated imagery."""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"
ASSETS.mkdir(exist_ok=True)
SCALE = 4
image = Image.new("RGBA", (256*SCALE, 256*SCALE))
d = ImageDraw.Draw(image)
def box(rect, fill, radius):
    d.rounded_rectangle(tuple(int(v*SCALE) for v in rect), radius=int(radius*SCALE), fill=fill)
def line(points, fill, width):
    points = [(int(x*SCALE), int(y*SCALE)) for x,y in points]
    d.line(points, fill=fill, width=int(width*SCALE), joint="curve")
    r=width*SCALE/2
    for x,y in points:
        d.ellipse((int(x-r),int(y-r),int(x+r),int(y+r)), fill=fill)

# An opaque charcoal app tile with a quiet inset edge.
box((12,12,244,244), "#101112", 53)
box((15,15,241,241), "#28292B", 50)
box((18,18,238,238), "#18191B", 47)

# Codex-inspired terminal mark, drawn as strokes rather than font glyphs.
line([(52,72),(87,102),(52,132)], "#F5F5F5", 13)
line([(105,132),(146,132)], "#F5F5F5", 12)

# Three management rails: the pink dial echoes the user's Codex accent.
for y, knob, color in [(75,184,"#F077AF"),(105,205,"#D7D8DA"),(135,178,"#D7D8DA")]:
    line([(170,y),(216,y)], "#65666A", 5)
    box((knob-5,y-8,knob+5,y+8), color, 4)

# The lower status strip anchors the icon and remains readable at desktop sizes.
box((49,174,215,201), "#303236", 10)
box((59,182,97,193), "#F077AF", 5)
box((107,182,144,193), "#DFE0E2", 5)
box((154,182,205,193), "#717379", 5)

image=image.resize((256,256),Image.Resampling.LANCZOS)
image.save(ASSETS / "codex-control-center.png")
image.save(ASSETS / "codex-control-center.ico",sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
print(ASSETS / "codex-control-center.ico")
