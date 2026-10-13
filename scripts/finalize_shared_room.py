from pathlib import Path
import hashlib, json, zipfile
from datetime import datetime, timezone

repo=Path(__file__).resolve().parents[1]
root=Path('D:/xAIDesk-Windows-0.3.2')
archive=Path(str(root)+'.zip')
if archive.exists(): raise SystemExit('Archive exists; do not overwrite.')
def entry(p):
    with p.open('rb') as f: digest=hashlib.file_digest(f,'sha256').hexdigest()
    return dict(path=str(p.relative_to(root)),bytes=p.stat().st_size,sha256=digest)
for item in json.loads((root/'files.json').read_text(encoding='utf-8-sig')):
    if entry(root/item['path'])!=item: raise SystemExit('Assembled package changed: '+item['path'])
manifest=dict(schema='sovereign-shared-room-beta-v1',version='0.3.2',status='local-beta-tested',created_utc=datetime.now(timezone.utc).isoformat(),
    automated_tests_passed=74,remote_https_smoke='passed with two clients on host PC',partner_device='not_yet_tested',live_ai_accounts='not_yet_tested',
    artifacts=[entry(root/p) for p in ['Setup.exe','payload/SovereignAI.DesktopPreview.exe','payload/SovereignAI.DesktopPreview.dll','payload/Sovereign.Desktop.Client.dll','payload/room-tunnel/cloudflared.exe']])
(root/'release-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
files=[entry(p) for p in sorted(root.rglob('*')) if p.is_file() and p.name!='files.json']
(root/'files.json').write_text(json.dumps(files,indent=2)+'\n',encoding='utf-8')
for p in root.rglob('*'):
    if p.is_file() and (p.name.lower() in {'.env','auth.json','transcript.json','credentials.json'} or p.suffix.lower() in {'.db','.sqlite'}): raise SystemExit('Unexpected account or room state in package: '+str(p))
with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for p in sorted(root.rglob('*')):
        if p.is_file(): z.write(p,Path(root.name)/p.relative_to(root))
with zipfile.ZipFile(archive) as z:
    if z.testzip(): raise SystemExit('ZIP verification failed')
    count=len(z.namelist())
with archive.open('rb') as f: digest=hashlib.file_digest(f,'sha256').hexdigest()
receipt=dict(archive=str(archive),sha256=digest,bytes=archive.stat().st_size,files=count,crc='passed',no_account_or_room_state=True)
(repo/'docs/desktop/evidence/shared-room-032/package-receipt.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8')
Path(str(archive)+'.sha256').write_text(digest+'  '+archive.name+'\n',encoding='ascii')
print(json.dumps(receipt))
