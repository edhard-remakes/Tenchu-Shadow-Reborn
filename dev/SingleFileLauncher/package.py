from pathlib import Path
import zipfile, sys, struct, hashlib
base=Path(__file__).resolve().parents[2]
payload=base/'performance'/'shadow-reborn-payload.zip'
if sys.argv[1]=='zip':
    with zipfile.ZipFile(payload,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=1) as z:
        for name in ('Tenchu-Shadow-Reborn-v49-Complete','Tenchu-PC/disc','texture-packs/tenchu-native'):
            for p in sorted((base/name).rglob('*')):
                if not p.is_file():continue
                rel=p.relative_to(base)
                if 'logs' in rel.parts or 'saves' in rel.parts or p.suffix in ('.pdb','.log','.cmd','.ps1'):continue
                if p.name=='TenchuSARecomp.exe':continue
                z.write(p,rel.as_posix().replace('Tenchu-Shadow-Reborn-v49-Complete/','Tenchu-Shadow-Reborn/',1))
    print('ZIP',payload.stat().st_size,flush=True)
else:
    out=base/'Entrega'/'Tenchu Shadow Reborn.exe'
    out.parent.mkdir(parents=True,exist_ok=True)
    host=base/'dev'/'SingleFileLauncher'/'publish'/'Tenchu Shadow Reborn.exe'
    with out.open('wb') as dst:
        for src in (host,payload):
            with src.open('rb') as f:
                while chunk:=f.read(4*1024*1024):dst.write(chunk)
        dst.write(struct.pack('<q',payload.stat().st_size));dst.write(b'TSRBPACK0001')
    print('EXE',out, out.stat().st_size,flush=True)






