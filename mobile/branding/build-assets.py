"""Prepare bundled mascot cutouts and canonical launcher art from reviewed AI originals.

Usage: python mobile/branding/build-assets.py
Requires Pillow. Generation prompts live alongside the source PNGs; API credentials are
read separately during image generation and never copied into this directory.
"""
from pathlib import Path
from collections import deque
from PIL import Image, ImageFilter, ImageOps
import json

root = Path(__file__).resolve().parents[2]
source = Path(__file__).resolve().parent / 'sources'
frontend = root / 'mobile/frontend'
resources = root / 'mobile/android/Resources'

def cutout(path):
    image = Image.open(path).convert('RGBA')
    rgb = image.convert('RGB')
    width, height = image.size
    pixels = rgb.load()
    outside = bytearray(width * height)
    queue = deque()
    def push(x,y):
        i=y*width+x
        if outside[i]: return
        r,g,b=pixels[x,y]
        if min(r,g,b)>=240 and max(r,g,b)-min(r,g,b)<15:
            outside[i]=255
            queue.append((x,y))
    for x in range(width): push(x,0); push(x,height-1)
    for y in range(height): push(0,y); push(width-1,y)
    while queue:
        x,y=queue.popleft()
        if x>0: push(x-1,y)
        if x+1<width: push(x+1,y)
        if y>0: push(x,y-1)
        if y+1<height: push(x,y+1)
    alpha=Image.frombytes('L', image.size, bytes(255-value for value in outside))
    # Remove only edge-connected white. White clothing and eye highlights stay opaque.
    image.putalpha(alpha)
    bounds=alpha.getbbox()
    if not bounds: raise RuntimeError('No visible mascot in '+str(path))
    image=image.crop(bounds)
    padding=18
    padded=Image.new('RGBA',(image.width+padding*2,image.height+padding*2))
    padded.alpha_composite(image,(padding,padding))
    return padded

def web(image,path,width):
    height=round(image.height*width/image.width)
    image.resize((width,height),Image.Resampling.LANCZOS).save(path,'WEBP',quality=91,method=6)

hero=ImageOps.mirror(cutout(source/'mascot-welcome.png'))
reading=ImageOps.mirror(cutout(source/'mascot-reading.png'))
web(hero,frontend/'mascot-welcome.webp',680)
web(reading,frontend/'mascot-reading.webp',360)
# Keep the complete character for the welcome composition, with a clean alpha channel.
hero.thumbnail((850,1100),Image.Resampling.LANCZOS)
(resources/'drawable/brand_welcome.webp').parent.mkdir(parents=True,exist_ok=True)
hero.save(resources/'drawable/brand_welcome.webp','WEBP',lossless=True,exact=True,method=6)
icon=Image.open(source/'launcher-chibi.png').convert('RGBA')
icon=icon.resize((1024,1024),Image.Resampling.LANCZOS)
icon.save(frontend/'icons/brand-avatar.png',optimize=True)
icon.resize((256,256),Image.Resampling.LANCZOS).save(resources/'drawable/brand_avatar.webp','WEBP',lossless=True,exact=True,method=6)
# Web header uses a small avatar instead of forcing the full launcher source to decode.
icon.resize((160,160),Image.Resampling.LANCZOS).save(frontend/'brand-avatar.webp','WEBP',quality=92,method=6)
# Android 12 applies its own launch icon crop. Leave generous transparent safe space.
launch=Image.new('RGBA',(512,512))
portrait=icon.resize((310,310),Image.Resampling.LANCZOS)
mask=Image.new('L',portrait.size)
from PIL import ImageDraw
ImageDraw.Draw(mask).ellipse((0,0,309,309),fill=255)
portrait.putalpha(mask)
launch.alpha_composite(portrait,(101,101))
launch.save(resources/'drawable/brand_splash_icon.webp','WEBP',lossless=True,exact=True,method=6)
# Android 同名资源只保留一个格式，旧版生成的 PNG 可安全移除。
for name in ['brand_welcome', 'brand_avatar', 'brand_splash_icon']:
    (resources/'drawable'/(name+'.png')).unlink(missing_ok=True)
report={'welcome_size':hero.size,'welcome_alpha':hero.getchannel('A').getextrema(),'reading_size':reading.size,'reading_alpha':reading.getchannel('A').getextrema(),'launcher_size':icon.size}
(root/'output/android-brand/asset-report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report))

# Additional app scenes use the same reviewed character. The originals stay outside
# the packaged frontend; only small transparent WebP files ship with the app.
scene_report = {}
for scene in ['home', 'search', 'collection', 'download', 'history', 'retry']:
    path = source / ('mascot-' + scene + '.png')
    if not path.exists():
        continue
    image = cutout(path)
    target = frontend / ('mascot-' + scene + '.webp')
    web(image, target, 192 if scene == 'home' else 360)
    scene_report[scene] = {'size': image.size, 'alpha': image.getchannel('A').getextrema(), 'bytes': target.stat().st_size}
report['scenes'] = scene_report
(root/'output/android-brand/asset-report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(scene_report))
