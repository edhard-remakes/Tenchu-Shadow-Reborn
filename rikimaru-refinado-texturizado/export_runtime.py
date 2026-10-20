from pathlib import Path
import runpy,struct
import numpy as np
p=Path(__file__).resolve().parent
r=runpy.run_path(str(p/'texture.py'))
origins=[]
for part in r['r']['data']['parts']:
    origins.append(np.array(part[2:5],float)+(origins[part[0]] if part[0]>=0 else 0))
vertices=[];flags=[]
for bone,(_,v,f) in enumerate(r['parts']):
    for i,face in enumerate(f):
        page,clut=r['keys_by_part'][bone][i]
        rgb=(128,128,128) if page>=0 else (clut&255,(clut>>8)&255,(clut>>16)&255)
        flags.append((int(page>=0),max(0,page),clut if page>=0 else 0))
        for j,index in enumerate(face):
            local=v[index]*[1000,-1000,-1000]-origins[bone]
            uv=np.rint(r['uv_by_part'][bone][i,j]).astype(int)
            vertices.append((bone,local,rgb,uv))
out=bytearray(struct.pack('<4sII',b'RHD2',len(vertices),len(flags)))
for bone,local,rgb,uv in vertices:
    out.extend(struct.pack('<if3f',bone,1,*local))
    out.extend(struct.pack('<if3f',bone,0,*local))
    out.extend(bytes(rgb)+b'\0'+struct.pack('<hh',*uv))
out.extend(np.arange(len(vertices),dtype='<i4').tobytes())
for textured,page,clut in flags:out.extend(struct.pack('<iHH',textured,page,clut))
target=p.parent/'dev/Tenchu-Windows/assets/rikimaru-refined.bin'
target.write_bytes(out)
print('Rigid native part binding:',len(vertices),'vertices;',len(flags),'triangles;',len(out),'bytes')
far_vertices=[];far_flags=[]
for bone,(_,v,f) in enumerate(r['parts']):
    _,original_v,original_f=r['r']['original'][bone]
    _,far_faces=r['r']['subdiv'](original_v,original_f)
    for i,face in enumerate(far_faces):
        uv2=r['uv_by_part'][bone];uv=np.array([uv2[i*4,0],uv2[i*4+1,1],uv2[i*4+2,2]])
        page,clut=r['keys_by_part'][bone][i*4]
        rgb=(128,128,128) if page>=0 else (clut&255,(clut>>8)&255,(clut>>16)&255)
        far_flags.append((int(page>=0),max(0,page),clut if page>=0 else 0))
        for j,index in enumerate(face):
            local=v[index]*[1000,-1000,-1000]-origins[bone]
            far_vertices.append((bone,local,rgb,np.rint(uv[j]).astype(int)))
out=bytearray(struct.pack('<4sII',b'RHD2',len(far_vertices),len(far_flags)))
for bone,local,rgb,uv in far_vertices:
    out.extend(struct.pack('<if3f',bone,1,*local)+struct.pack('<if3f',bone,0,*local))
    out.extend(bytes(rgb)+b'\0'+struct.pack('<hh',*uv))
out.extend(np.arange(len(far_vertices),dtype='<i4').tobytes())
for textured,page,clut in far_flags:out.extend(struct.pack('<iHH',textured,page,clut))
target.with_name('rikimaru-refined-far.bin').write_bytes(out)
print('Distance LOD:',len(far_flags),'triangles; same refined control points and HD UVs')
