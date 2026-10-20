from pathlib import Path
import re,json,zlib,hashlib
from PIL import Image

root=Path(__file__).resolve().parents[1]
source=root/'texture-packs/tenchu-downloaded/SLUS-00706/replacements'
dest=root/'texture-packs/tenchu-native'
dest.mkdir(exist_ok=True)
rx=re.compile(r'tex(page|upload)-(P4|P8|STP4|STP8|C16|STC16)-([0-9A-Fa-f]{16})-(?:([0-9A-Fa-f]{16})-)?(\d+)x(\d+)-(\d+)-(\d+)-(\d+)x(\d+)(?:-P(\d+)-(\d+))?$')
items=[]; skipped=[]; names=set()
for path in sorted(source.rglob('*.png')):
    m=rx.fullmatch(path.stem)
    if not m: skipped.append(str(path.relative_to(source)));continue
    g=m.groups(); mode={'P4':0,'P8':1,'C16':2}[g[1].replace('STP','P').replace('STC','C')]
    key=path.stem
    if key in names: continue
    names.add(key)
    file=hashlib.sha256(str(path.relative_to(source)).encode()).hexdigest()[:24]+'.rgba.z'
    with Image.open(path) as im:
        im=im.convert('RGBA');w,h=im.size
        if not (dest/file).exists(): (dest/file).write_bytes(zlib.compress(im.tobytes(),3))
    items.append(dict(Type=g[0],Mode=mode,Semitransparent=g[1].startswith('ST'),Hash=g[2],Palette=g[3] or '0',SourceWidth=int(g[4]),SourceHeight=int(g[5]),
        X=int(g[6]),Y=int(g[7]),Width=int(g[8]),Height=int(g[9]),PaletteMin=int(g[10] or 0),PaletteMax=int(g[11] or 0),
        ImageWidth=w,ImageHeight=h,File=file,Original=str(path.relative_to(source))))
    if len(items)%200==0: print(f'prepared {len(items)}',flush=True)
(dest/'index.json').write_text(json.dumps(items),encoding='utf8')
(dest/'skipped.json').write_text(json.dumps(skipped),encoding='utf8')
print(json.dumps({'prepared':len(items),'skipped':len(skipped)}))


