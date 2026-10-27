"""Render the original code-native broker icon; Pillow is only needed to regenerate assets."""
from pathlib import Path
from PIL import Image, ImageDraw

target = Path(__file__).resolve().parents[1] / 'src/SshBroker.Gui/Assets'
target.mkdir(exist_ok=True)
svg = '''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
<rect x="8" y="8" width="240" height="240" rx="56" fill="#4263EB"/>
<path d="M164 128h28V80h20M192 128v48h20" fill="none" stroke="#A5ECF5" stroke-width="12" stroke-linecap="round" stroke-linejoin="round"/>
<rect x="38" y="68" width="132" height="120" rx="20" fill="#4263EB" stroke="#FFFFFF" stroke-width="12"/>
<path d="m64 108 22 20-22 20m42 0h30" fill="none" stroke="#FFFFFF" stroke-width="12" stroke-linecap="round" stroke-linejoin="round"/>
<circle cx="212" cy="80" r="12" fill="#A5ECF5"/><circle cx="212" cy="176" r="12" fill="#A5ECF5"/>
</svg>'''
(target / 'SshBroker.svg').write_text(svg, encoding='utf-8')
scale = 4
im = Image.new('RGBA', (256*scale, 256*scale))
d = ImageDraw.Draw(im)
def rect(box, radius, fill, outline=None, width=0):
    d.rounded_rectangle(tuple(v*scale for v in box), radius=radius*scale, fill=fill, outline=outline, width=width*scale)
def line(points, fill):
    d.line([(x*scale,y*scale) for x,y in points], fill=fill, width=12*scale, joint='curve')
    for x,y in points:
        d.ellipse(((x-6)*scale,(y-6)*scale,(x+6)*scale,(y+6)*scale),fill=fill)
blue, white, cyan = '#4263EB', '#FFFFFF', '#A5ECF5'
rect((8,8,248,248),56,blue)
line([(164,128),(192,128),(192,80),(212,80)],cyan)
line([(192,128),(192,176),(212,176)],cyan)
rect((32,62,176,194),26,white)
rect((44,74,164,182),14,blue)
line([(64,108),(86,128),(64,148)],white)
line([(106,148),(136,148)],white)
for x,y in [(212,80),(212,176)]:
    d.ellipse(((x-12)*scale,(y-12)*scale,(x+12)*scale,(y+12)*scale), fill=cyan)
im = im.resize((256,256),Image.Resampling.LANCZOS)
im.save(target / 'SshBroker.png')
im.save(target / 'SshBroker.ico', sizes=[(n,n) for n in [16,20,24,32,40,48,64,128,256]])
print('Generated SVG, PNG and multi-resolution ICO.')
