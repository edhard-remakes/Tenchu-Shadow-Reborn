from pathlib import Path
import runpy,json,zlib,struct,math,os
import numpy as np
from PIL import Image
p=Path(__file__).resolve().parent;base=p.parent
r=runpy.run_path(str(p/'texture.py'))
regions={(e['Page'],e['Clut']):sorted(e['Regions'],key=lambda a:a['Width']*a['Height']) for e in json.loads((p/'hd-regions.json').read_text())}
patches={};mapping={};hd_faces=0
override_dir=Path(os.environ.get('TENCHU_REFINED_TEXTURE_OVERRIDES',str(p/'remaster-v1/edits')))
overrides=[]
manifest_path=override_dir/'manifest.json'
manifest=json.loads(manifest_path.read_text()) if manifest_path.is_file() else {}
aliases=manifest.get('aliases',{})
if manifest.get('cloth_untextured'):
    for bone in (0,1):
        _,v,f=r['parts'][bone]
        xy=(v[:,:2]-v[:,:2].min(axis=0))/np.maximum(np.ptp(v[:,:2],axis=0),1e-6)*255
        for i,face in enumerate(f):
            if r['keys_by_part'][bone][i][0]<0:
                r['keys_by_part'][bone][i]=(-2,0)
                r['uv_by_part'][bone][i]=xy[face]
def material(key,uv):
    if key[0]==-2:
        name=manifest['cloth_untextured']
        if name not in patches:patches[name]=np.asarray(Image.open(override_dir/name).convert('RGBA'))
        return name,{'X':0,'Y':0,'Width':256,'Height':256}
    for e in regions.get(key,[]):
        if np.all(uv.min(axis=0)>=[e['X'],e['Y']]) and np.all(uv.max(axis=0)<=[e['X']+e['Width'],e['Y']+e['Height']]):
            name=aliases.get(e['File'],e['File'])
            if name not in patches:
                override=override_dir/(name if name.endswith('.png') else name+'.png')
                if override.is_file():
                    patches[name]=np.asarray(Image.open(override).convert('RGBA'));overrides.append(name)
                else:
                    pixels=zlib.decompress((base/'texture-packs/tenchu-native'/name).read_bytes())
                    patches[name]=np.frombuffer(pixels,np.uint8).reshape(e['ImageHeight'],e['ImageWidth'],4)
            return name,e
    name='native-'+str(key)
    if name not in patches:patches[name]=r['textures'][key] if key[0]>=0 else r['textures'][key][:1,:1]
    return name,None
for bone,(_,v,f) in enumerate(r['parts']):
    for i in range(len(f)):
        key=r['keys_by_part'][bone][i];uv=r['uv_by_part'][bone][i]
        mapping[bone,i]=material(key,uv)
        hd_faces+=int(mapping[bone,i][1] is not None)
width=4096;x=y=2;row_height=0;placements={}
for name,a in sorted(patches.items(),key=lambda pair:-pair[1].shape[0]):
    h,w=a.shape[:2]
    if x+w+2>width:x=2;y+=row_height+4;row_height=0
    placements[name]=(x,y);x+=w+4;row_height=max(row_height,h)
height=2**math.ceil(math.log2(y+row_height+2));assert height<=8192
atlas=np.zeros((height,width,4),np.uint8)
for name,a in patches.items():
    x,y=placements[name];h,w=a.shape[:2]
    atlas[y-2:y+h+2,x-2:x+w+2]=np.pad(a,((2,2),(2,2),(0,0)),mode='edge')
assets=Path(os.environ.get('TENCHU_REFINED_ASSET_OUTPUT',str(base/'dev/Tenchu-Windows/assets')))
assets.mkdir(parents=True,exist_ok=True)
(assets/'rikimaru-refined-atlas.rgba.z').write_bytes(struct.pack('<II',width,height)+zlib.compress(atlas.tobytes(),3))
Image.fromarray(atlas).save(p/'HD-atlas.png')
origins=[]
for part in r['r']['data']['parts']:origins.append(np.array(part[2:5],float)+(origins[part[0]] if part[0]>=0 else 0))
def export(far=False):
    vertices=[];flags=[]
    for bone,(_,v,f) in enumerate(r['parts']):
        if far:
            _,ov,of=r['r']['original'][bone];_,f=r['r']['subdiv'](ov,of)
        for i,face in enumerate(f):
            original_index=i*4 if far else i
            if far:
                uv2=r['uv_by_part'][bone];uv=np.array([uv2[i*4,0],uv2[i*4+1,1],uv2[i*4+2,2]])
            else:uv=r['uv_by_part'][bone][i]
            key=r['keys_by_part'][bone][original_index]
            name,e=material(key,uv);a=patches[name];origin=np.array(placements[name])
            if e:
                pixels=(uv-[e['X'],e['Y']])*[a.shape[1]/e['Width'],a.shape[0]/e['Height']]
                pixels=np.clip(pixels,[0,0],[a.shape[1]-1,a.shape[0]-1])
            elif key[0]>=0:pixels=uv
            else:pixels=np.zeros((3,2))
            flags.append((1,0x10F,0x7FFF))
            for j,index in enumerate(face):
                local=v[index]*[1000,-1000,-1000]-origins[bone]
                vertices.append((bone,local,np.rint(origin+pixels[j]).astype(int)))
    out=bytearray(struct.pack('<4sII',b'RHD2',len(vertices),len(flags)))
    for bone,local,uv in vertices:
        out.extend(struct.pack('<if3f',bone,1,*local)+struct.pack('<if3f',bone,0,*local))
        out.extend(bytes([128,128,128,0])+struct.pack('<hh',*uv))
    out.extend(np.arange(len(vertices),dtype='<i4').tobytes())
    for textured,page,clut in flags:out.extend(struct.pack('<iHH',textured,page,clut))
    (assets/('rikimaru-refined-far.bin' if far else 'rikimaru-refined.bin')).write_bytes(out)
    return len(flags)
report={'near_triangles':export(),'far_triangles':export(True),'hd_faces':hd_faces,'atlas':[width,height],'patches':len(patches),
        'hd_files':sum(not k.startswith('native-') for k in patches),'overrides':overrides}
(p/'runtime-hd-report.json').write_text(json.dumps(report,indent=2));print(report)
