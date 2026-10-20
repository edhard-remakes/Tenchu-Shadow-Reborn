from pathlib import Path
import json, struct, math
import numpy as np
from PIL import Image,ImageDraw

OUT=Path(__file__).resolve().parent
data=json.loads((OUT.parent/'rikimaru-original/geometry.json').read_text())
allv=np.array(data['vertices'],float)
allf=np.array(data['triangles'],int)
groups=np.array(data['groups'],int)

def clean(v,f):
    keep=np.linalg.norm(np.cross(v[f[:,1]]-v[f[:,0]],v[f[:,2]]-v[f[:,0]]),axis=1)>1e-10
    f=f[keep]
    _,idx=np.unique(np.sort(f,axis=1),axis=0,return_index=True)
    return v,f[np.sort(idx)]

def edges(f):
    e={}
    for a,b,c in f:
        for i,j in ((a,b),(b,c),(c,a)):
            key=tuple(sorted((int(i),int(j))))
            e[key]=e.get(key,0)+1
    return e

def subdiv(v,f):
    vv=list(v); mids={}
    def mid(a,b):
        k=tuple(sorted((int(a),int(b))))
        if k not in mids:mids[k]=len(vv);vv.append((v[a]+v[b])/2)
        return mids[k]
    ff=[]
    for a,b,c in f:
        ab,bc,ca=mid(a,b),mid(b,c),mid(c,a)
        ff.extend(((a,ab,ca),(ab,b,bc),(ca,bc,c),(ab,bc,ca)))
    return np.array(vv),np.array(ff)

def refine(v,f):
    # Keep open part interfaces stationary so rigid PS1 pieces still meet.
    for _ in range(2):v,f=subdiv(v,f)
    origin=v.copy();links=[set() for _ in v];locked=set()
    for (a,b),n in edges(f).items():
        links[a].add(b);links[b].add(a)
        if n!=2:locked.update((a,b))
    mobile=np.array([i not in locked and len(links[i])>0 for i in range(len(v))])
    limit=np.linalg.norm(np.ptp(v,axis=0))*.035
    # Small paired smoothing steps remove facets without shrinking the body.
    for _ in range(8):
        for weight in (.45,-.47):
            delta=np.zeros_like(v)
            for i,neighbors in enumerate(links):
                if mobile[i]:delta[i]=v[list(neighbors)].mean(axis=0)-v[i]
            v+=weight*delta
            offset=v-origin;length=np.linalg.norm(offset,axis=1)
            v=origin+offset*np.minimum(1,limit/np.maximum(length,1e-12))[:,None]
    assert np.allclose(v[~mobile],origin[~mobile])
    return v,f

def normals(v,f):
    n=np.zeros_like(v)
    cross=np.cross(v[f[:,1]]-v[f[:,0]],v[f[:,2]]-v[f[:,0]])
    for i in range(3):np.add.at(n,f[:,i],cross)
    return n/np.maximum(np.linalg.norm(n,axis=1)[:,None],1e-12)

original=[];parts=[]
for g in sorted(set(groups)):
    f=allf[groups==g];ids=np.unique(f);mapping={int(x):i for i,x in enumerate(ids)}
    v=allv[ids];f=np.array([[mapping[int(i)] for i in t] for t in f])
    v,f=clean(v,f)
    if not len(f):continue
    original.append((f'part_{g:02d}',v.copy(),f.copy()))
    v,f=refine(v,f);parts.append((f'part_{g:02d}',v,f))

lines=['# Rikimaru: refined reference mesh. Separate rigid parts; no textures or rig.','s 1']
offset=1
for name,v,f in parts:
    lines.append('o '+name)
    lines.extend('v %.7f %.7f %.7f'%tuple(p) for p in v)
    lines.extend('f '+' '.join(str(int(i)+offset) for i in t) for t in f)
    offset+=len(v)
(OUT/'Rikimaru-refinado.obj').write_text('\n'.join(lines)+'\n')

# Standard binary glTF, editable in Blender, including smooth vertex normals.
blob=bytearray();views=[];accessors=[];meshes=[];nodes=[]
def accessor(array,component,kind,target):
    while len(blob)%4:blob.append(0)
    start=len(blob);blob.extend(array.tobytes())
    views.append({'buffer':0,'byteOffset':start,'byteLength':array.nbytes,'target':target})
    a={'bufferView':len(views)-1,'componentType':component,'count':len(array),'type':kind}
    if kind=='VEC3':a.update(min=array.min(axis=0).tolist(),max=array.max(axis=0).tolist())
    accessors.append(a);return len(accessors)-1
for name,v,f in parts:
    p=accessor(v.astype('<f4'),5126,'VEC3',34962)
    n=accessor(normals(v,f).astype('<f4'),5126,'VEC3',34962)
    idx=accessor(f.astype('<u4').reshape(-1),5125,'SCALAR',34963)
    meshes.append({'name':name,'primitives':[{'attributes':{'POSITION':p,'NORMAL':n},'indices':idx,'material':0}]})
    nodes.append({'name':name,'mesh':len(meshes)-1})
doc={'asset':{'version':'2.0','generator':'Tenchu Shadow Reborn mesh refinement'},'scene':0,'scenes':[{'nodes':list(range(len(nodes)))}],
     'nodes':nodes,'meshes':meshes,'buffers':[{'byteLength':len(blob)}],'bufferViews':views,'accessors':accessors,
     'materials':[{'name':'Reference clay','doubleSided':True,'pbrMetallicRoughness':{'baseColorFactor':[.52,.57,.62,1],'metallicFactor':0,'roughnessFactor':.8}}]}
js=json.dumps(doc,separators=(',',':')).encode()
js+=b' '*((-len(js))%4);blob.extend(b'\0'*((-len(blob))%4))
glb=struct.pack('<III',0x46546c67,2,12+8+len(js)+8+len(blob))+struct.pack('<II',len(js),0x4e4f534a)+js+struct.pack('<II',len(blob),0x004e4942)+blob
(OUT/'Rikimaru-refinado.glb').write_bytes(glb)

def render(part_list,angle,smooth):
    w,h=440,690
    pixels=np.full((h,w,3),235,np.uint8);depth=np.full((h,w),-np.inf)
    rot=np.array([[math.cos(angle),0,math.sin(angle)],[0,1,0],[-math.sin(angle),0,math.cos(angle)]])
    light=np.array([-.35,.6,.7]);light/=np.linalg.norm(light)
    for name,v,f in part_list:
        v=v@rot.T;n=normals(v,f)
        xy=np.column_stack((w/2+v[:,0]*300,h-35-v[:,1]*300))
        for face in f:
            p=xy[face];xmin=max(0,int(np.floor(p[:,0].min())));xmax=min(w-1,int(np.ceil(p[:,0].max())))
            ymin=max(0,int(np.floor(p[:,1].min())));ymax=min(h-1,int(np.ceil(p[:,1].max())))
            if xmin>xmax or ymin>ymax:continue
            yy,xx=np.mgrid[ymin:ymax+1,xmin:xmax+1];xx=xx+.5;yy=yy+.5
            a,b,c=p;den=(b[1]-c[1])*(a[0]-c[0])+(c[0]-b[0])*(a[1]-c[1])
            if abs(den)<1e-8:continue
            aa=((b[1]-c[1])*(xx-c[0])+(c[0]-b[0])*(yy-c[1]))/den
            bb=((c[1]-a[1])*(xx-c[0])+(a[0]-c[0])*(yy-c[1]))/den;cc=1-aa-bb
            z=aa*v[face[0],2]+bb*v[face[1],2]+cc*v[face[2],2]
            target=depth[ymin:ymax+1,xmin:xmax+1];mask=(aa>=0)&(bb>=0)&(cc>=0)&(z>target)
            if not mask.any():continue
            if smooth:
                nn=aa[:,:,None]*n[face[0]]+bb[:,:,None]*n[face[1]]+cc[:,:,None]*n[face[2]]
                nn/=np.maximum(np.linalg.norm(nn,axis=2,keepdims=True),1e-8)
            else:
                fn=np.cross(v[face[1]]-v[face[0]],v[face[2]]-v[face[0]]);fn/=max(np.linalg.norm(fn),1e-8)
                nn=np.broadcast_to(fn,(*aa.shape,3))
            shade=.36+.64*np.abs(nn@light)
            rgb=np.clip(shade[:,:,None]*np.array([144,158,173]),0,255).astype(np.uint8)
            pixels[ymin:ymax+1,xmin:xmax+1][mask]=rgb[mask];target[mask]=z[mask]
    return Image.fromarray(pixels)
preview=Image.new('RGB',(1320,750),'#ebebeb')
for i,(model,angle,smooth,label) in enumerate(((original,0,False,'Original'),(parts,0,True,'Refinado - frente'),(parts,.65,True,'Refinado - 3/4'))):
    preview.paste(render(model,angle,smooth),(i*440,45));ImageDraw.Draw(preview).text((i*440+28,18),label,fill='#222222')
preview.save(OUT/'comparacao.png')
report={'original_triangles':sum(len(f) for _,_,f in original),'refined_triangles':sum(len(f) for _,_,f in parts),'parts':len(parts),
        'vertices':sum(len(v) for _,v,_ in parts),'has_textures':False,'has_rig':False,'pose':'original arms down'}
(OUT/'relatorio.json').write_text(json.dumps(report,indent=2));print(report)
