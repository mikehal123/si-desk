from pathlib import Path
import hashlib, json, shutil, zipfile

repo=Path(__file__).resolve().parents[1]
package=Path('D:/xAIDesk-Windows-0.3.2')
if package.exists(): raise SystemExit('Package exists; do not overwrite.')
package.mkdir()
shutil.copytree(repo/'artifacts/shared-room-032/app',package/'payload')
shutil.copytree(Path('D:/xAIDesk-Windows-0.3.1/payload/engine-bundle'),package/'payload/engine-bundle')
shutil.copyfile(repo/'artifacts/shared-room-032/setup/SovereignAI.DesktopPreview.Setup.exe',package/'Setup.exe')
shutil.copyfile(repo/'packaging/windows/Install-SovereignAI.ps1',package/'SovereignAI.ps1')
for name in ['Install.cmd','Launch.cmd','Repair.cmd','Uninstall.cmd','CHAT-CONNECTIONS.md','IMIGROK-MIT-NOTICE.txt']:
    shutil.copyfile(Path('D:/xAIDesk-Windows-0.3.1')/name,package/name)
shutil.copyfile(repo/'docs/desktop/SHARED-ROOM-032.md',package/'START-HERE.md')
def entry(path):
    with path.open('rb') as f: sha=hashlib.file_digest(f,'sha256').hexdigest()
    return dict(path=str(path.relative_to(package)),bytes=path.stat().st_size,sha256=sha)
files=[entry(p) for p in sorted(package.rglob('*')) if p.is_file()]
(package/'files.json').write_text(json.dumps(files,indent=2)+'\n',encoding='utf-8')
print(json.dumps(dict(package=str(package),files=len(files))))
