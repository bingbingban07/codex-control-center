from PIL import Image, ImageDraw
from pathlib import Path

root=Path(__file__).resolve().parents[1]
image=Image.new('RGBA',(256,256),(0,0,0,0));draw=ImageDraw.Draw(image)
draw.rounded_rectangle((8,8,248,248),radius=58,fill='#202423',outline='#4B5552',width=3)
draw.rounded_rectangle((42,44,214,182),radius=22,fill='#263B37',outline='#9DC7BA',width=8)
draw.ellipse((155,67,180,92),fill='#C8E2D9')
draw.polygon([(48,162),(94,105),(139,159),(164,127),(208,169),(208,176),(48,176)],fill='#83B6A6')
draw.line([(86,205),(101,216),(86,227)],fill='#E8F4EF',width=8)
draw.line([(117,227),(166,227)],fill='#E8F4EF',width=7)
image.save(root/'assets'/'wallpaper-icon.png')
image.save(root/'assets'/'wallpaper.ico',sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
