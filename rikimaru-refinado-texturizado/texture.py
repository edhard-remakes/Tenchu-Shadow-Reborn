from pathlib import Path
import json,struct,runpy,io,math
import numpy as np
from PIL import Image,ImageDraw

OUT=Path(__file__).resolve().parent
base=OUT.parent
r=runpy.run_path(str(base/'rikimaru-refinado-v1/refine.py'))
parts=r['parts']; allv=r['allv']; allf=r['allf'];groups=r['groups'];normals=r['normals']
mad=(base/'rikimaru-original/@187_RIKIMARU.MAD').read_bytes()
tpd=(base/'rikimaru-original/@187_RIKIMARU.TPD').read_bytes()
vram=np.zeros((512,1024),dtype=np.uint16)
for off in struct.unpack_from('<23I',tpd,8):
    p=8+off;magic,flags=struct.unpack_from('<II',tpd,p);assert magic==16;p+=8
    for _ in range(2 if flags&8 else 1):
        length,x,y,w,h=struct.unpack_from('<I4H',tpd,p)
        pixels=np.frombuffer(tpd,dtype='<u2',count=w*h,offset=p+12).reshape(h,w)
        vram[y:y+h,x:x+w]=pixels;p+=length

textures={};uv_by_part=[];keys_by_part=[]
for g in range(15):
    tmd=8+struct.unpack_from('<I',mad,8+g*16+12)[0];origin=tmd+12
    vo,nv,no,nn,po,nf,scale=struct.unpack_from('<7I',mad,origin)
    p=origin+po;uvs=[];keys=[]
    for _ in range(nf):
        ol,il,flag,mode=struct.unpack_from('4B',mad,p);count=4 if mode&8 else 3
        if mode&4:
            points=[np.array(struct.unpack_from('2B',mad,p+4+j*4),float) for j in range(count)]
            clut=struct.unpack_from('<H',mad,p+6)[0];page=struct.unpack_from('<H',mad,p+10)[0]
        else:
            rgb=np.array([struct.unpack_from('3B',mad,p+4+j*4) for j in range(count if mode&16 else 1)]).mean(axis=0).astype(int)
            page=-1;clut=int(rgb[0])|(int(rgb[1])<<8)|(int(rgb[2])<<16)
            points=[np.zeros(2) for _ in range(count)]
        order=[0,1,3,2] if count==4 else [0,1,2]
        for k in range(1,count-1):
            uvs.append(np.array([points[order[i]] for i in (0,k,k+1)]));keys.append((page,clut))
        p+=4+il*4
    f=allf[groups==g];assert len(f)==len(uvs)
    keep=np.linalg.norm(np.cross(allv[f[:,1]]-allv[f[:,0]],allv[f[:,2]]-allv[f[:,0]]),axis=1)>1e-10
    idx=np.flatnonzero(keep);_,unique=np.unique(np.sort(f[keep],axis=1),axis=0,return_index=True)
    idx=idx[np.sort(unique)];uvs=[uvs[i] for i in idx];keys=[keys[i] for i in idx]
    for _ in range(2):
        uu=[];kk=[]
        for (a,b,c),key in zip(uvs,keys):
            ab,bc,ca=(a+b)/2,(b+c)/2,(c+a)/2
            uu.extend((np.array([a,ab,ca]),np.array([ab,b,bc]),np.array([ca,bc,c]),np.array([ab,bc,ca])))
            kk.extend([key]*4)
        uvs,keys=uu,kk
    assert len(uvs)==len(parts[g][2]);uv_by_part.append(np.array(uvs));keys_by_part.append(keys)

def decode(key):
    if key in textures:return textures[key]
    page,clut=key
    if page==-1:
        a=np.zeros((256,256,4),np.uint8);a[:]=[clut&255,(clut>>8)&255,(clut>>16)&255,255]
        textures[key]=a;return a
    mode=(page>>7)&3;assert mode==0
    x=(page&15)*64;y=((page>>4)&1)*256
    yy,xx=np.mgrid[0:256,0:256]
    words=vram[(y+yy)&511,(x+xx//4)&1023];indices=(words>>((xx%4)*4))&15
    cx=(clut&63)*16;cy=(clut>>6)&511;pal=vram[cy,cx:cx+16];colors=pal[indices]
    a=np.empty((256,256,4),np.uint8)
    for k in range(3):a[:,:,k]=(((colors>>(k*5))&31)*255//31).astype(np.uint8)
    a[:,:,3]=np.where(colors==0,0,255)
    textures[key]=a
    Image.fromarray(a).save(OUT/f'texture-{page:04x}-{clut:04x}.png')
    return a
for keys in keys_by_part:
    for key in set(keys):decode(key)

blob=bytearray();views=[];accessors=[];meshes=[];nodes=[];images=[];materials=[];tex=[]
def view(raw,target=None):
    blob.extend(b'\0'*((-len(blob))%4));start=len(blob);blob.extend(raw)
    d={'buffer':0,'byteOffset':start,'byteLength':len(raw)}
    if target:d['target']=target
    views.append(d);return len(views)-1
def acc(a,component,kind,target):
    d={'bufferView':view(a.tobytes(),target),'componentType':component,'count':len(a),'type':kind}
    if kind=='VEC3':d.update(min=a.min(axis=0).tolist(),max=a.max(axis=0).tolist())
    accessors.append(d);return len(accessors)-1
key_material={}
for key,a in textures.items():
    stream=io.BytesIO();Image.fromarray(a).save(stream,format='PNG')
    images.append({'bufferView':view(stream.getvalue()),'mimeType':'image/png'})
    tex.append({'sampler':0,'source':len(images)-1})
    materials.append({'name':f'Original PS1 {key[0]:04x}-{key[1]:04x}','doubleSided':True,'alphaMode':'MASK','alphaCutoff':.1,
        'pbrMetallicRoughness':{'baseColorTexture':{'index':len(tex)-1},'metallicFactor':0,'roughnessFactor':.9}})
    key_material[key]=len(materials)-1
for g,(name,v,f) in enumerate(parts):
    nn=normals(v,f);prims=[];keys=keys_by_part[g];uvs=uv_by_part[g]
    for key in sorted(set(keys)):
        ids=[i for i,k in enumerate(keys) if k==key];tri=f[ids]
        positions=v[tri].reshape(-1,3).astype('<f4');n=nn[tri].reshape(-1,3).astype('<f4')
        uv=((uvs[ids].reshape(-1,2)+.5)/256).astype('<f4')
        p=acc(positions,5126,'VEC3',34962);normal=acc(n,5126,'VEC3',34962);uv_idx=acc(uv,5126,'VEC2',34962)
        indices=acc(np.arange(len(positions),dtype='<u4'),5125,'SCALAR',34963)
        prims.append({'attributes':{'POSITION':p,'NORMAL':normal,'TEXCOORD_0':uv_idx},'indices':indices,'material':key_material[key]})
    meshes.append({'name':name,'primitives':prims});nodes.append({'name':name,'mesh':g})
doc={'asset':{'version':'2.0','generator':'Tenchu Shadow Reborn original UV recovery'},'scene':0,'scenes':[{'nodes':list(range(15))}],
     'nodes':nodes,'meshes':meshes,'accessors':accessors,'bufferViews':views,'buffers':[{'byteLength':len(blob)}],
     'materials':materials,'textures':tex,'images':images,'samplers':[{'magFilter':9728,'minFilter':9728,'wrapS':10497,'wrapT':10497}]}
js=json.dumps(doc,separators=(',',':')).encode();js+=b' '*((-len(js))%4);blob+=b'\0'*((-len(blob))%4)
glb=struct.pack('<III',0x46546c67,2,12+8+len(js)+8+len(blob))+struct.pack('<II',len(js),0x4e4f534a)+js+struct.pack('<II',len(blob),0x004e4942)+blob
(OUT/'Rikimaru-refinado-texturizado.glb').write_bytes(glb)

def render(angle):
    w,h=480,740;pixels=np.full((h,w,3),226,np.uint8);depth=np.full((h,w),-np.inf)
    rot=np.array([[math.cos(angle),0,math.sin(angle)],[0,1,0],[-math.sin(angle),0,math.cos(angle)]])
    light=np.array([-.25,.4,.85]);light/=np.linalg.norm(light)
    for g,(name,v,f) in enumerate(parts):
        v=v@rot.T;nn=normals(v,f);xy=np.column_stack((w/2+v[:,0]*330,h-30-v[:,1]*330))
        for ti,face in enumerate(f):
            a,b,c=xy[face];p=xy[face];xmin=max(0,int(np.floor(p[:,0].min())));xmax=min(w-1,int(np.ceil(p[:,0].max())))
            ymin=max(0,int(np.floor(p[:,1].min())));ymax=min(h-1,int(np.ceil(p[:,1].max())))
            if xmin>xmax or ymin>ymax:continue
            yy,xx=np.mgrid[ymin:ymax+1,xmin:xmax+1];xx=xx+.5;yy=yy+.5
            den=(b[1]-c[1])*(a[0]-c[0])+(c[0]-b[0])*(a[1]-c[1])
            if abs(den)<1e-8:continue
            aa=((b[1]-c[1])*(xx-c[0])+(c[0]-b[0])*(yy-c[1]))/den
            bb=((c[1]-a[1])*(xx-c[0])+(a[0]-c[0])*(yy-c[1]))/den;cc=1-aa-bb
            z=aa*v[face[0],2]+bb*v[face[1],2]+cc*v[face[2],2]
            uv=aa[:,:,None]*uv_by_part[g][ti,0]+bb[:,:,None]*uv_by_part[g][ti,1]+cc[:,:,None]*uv_by_part[g][ti,2]
            sample=textures[keys_by_part[g][ti]][np.rint(uv[:,:,1]).astype(int)%256,np.rint(uv[:,:,0]).astype(int)%256]
            target=depth[ymin:ymax+1,xmin:xmax+1];mask=(aa>=0)&(bb>=0)&(cc>=0)&(z>target)&(sample[:,:,3]>0)
            normal=aa[:,:,None]*nn[face[0]]+bb[:,:,None]*nn[face[1]]+cc[:,:,None]*nn[face[2]]
            normal/=np.maximum(np.linalg.norm(normal,axis=2,keepdims=True),1e-9)
            shade=.65+.35*np.abs(normal@light)
            color=np.clip(sample[:,:,:3]*shade[:,:,None],0,255).astype(np.uint8)
            pixels[ymin:ymax+1,xmin:xmax+1][mask]=color[mask];target[mask]=z[mask]
    return Image.fromarray(pixels)
preview=Image.new('RGB',(1440,790),'#e2e2e2')
for i,(angle,name) in enumerate(((0,'Frente'),(.65,'3/4'),(math.pi,'Costas'))):
    preview.paste(render(angle),(i*480,35));ImageDraw.Draw(preview).text((i*480+25,14),name,fill='#222222')
preview.save(OUT/'Rikimaru-texturizado-preview.png')
print('Exported',len(materials),'original materials,',sum(len(f) for _,_,f in parts),'triangles; GLB',len(glb),'bytes')

