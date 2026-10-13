"""Create a new beta without modifying any previously published release."""
from pathlib import Path
import argparse, hashlib, json, shutil
repo = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--version', default='0.3.5')
parser.add_argument('--previous', default='0.3.4')
parser.add_argument('--publish', default='artifacts/cursor-035-final/app')
parser.add_argument('--output-root', default='D:/', help='Destination drive or folder for the new release')
args = parser.parse_args()
release = Path(args.output_root) / ('xAIDesk-Windows-' + args.version)
if release.exists(): raise SystemExit('Release exists; refusing to overwrite')
previous = Path('D:/xAIDesk-Windows-' + args.previous)
release.mkdir()
shutil.copytree(repo/args.publish, release/'payload')
shutil.copytree(previous/'payload/engine-bundle', release/'payload/engine-bundle')
for name in ['Setup.exe','Install.cmd','Launch.cmd','Repair.cmd','Uninstall.cmd','CHAT-CONNECTIONS.md','IMIGROK-MIT-NOTICE.txt','INTEGRATION-PROJECT.md']:
    shutil.copyfile(previous/name, release/name)
installer=(previous/'SovereignAI.ps1').read_text(encoding='utf-8-sig').replace("$version='"+args.previous+"'", "$version='"+args.version+"'")
installer=installer.replace('xAI Desk '+args.previous+' â€” connected workspace beta','xAI Desk '+args.version+' â€” Cursor chat beta').replace('xAI Desk '+args.previous+' â€” Cursor chat beta','xAI Desk '+args.version+' â€” Cursor chat beta')
import re
installer=re.sub(r"(?m)^        \$shortcut.Description=.*$", "        $shortcut.Description='xAI Desk "+args.version+" - desktop beta'", installer)
if (release/'payload/Assets/SovereignAI.ico').exists() and '$shortcut.IconLocation=' not in installer:
    installer=installer.replace('        $shortcut.Save()', "        $shortcut.IconLocation=(Join-Path $app 'Assets\\SovereignAI.ico')+',0'\n        $shortcut.Save()")
(release/'SovereignAI.ps1').write_text(installer,encoding='utf-8-sig')
shutil.copyfile(repo/'scripts/Install-CursorConnection.ps1',release/'Install-CursorConnection.ps1')
(release/'Install-CursorConnection.cmd').write_text('@echo off\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-CursorConnection.ps1"\npause\n',encoding='utf-8')
(release/'START-HERE.md').write_text((repo/'docs/desktop/CURSOR-CONNECTION-035.md').read_text(encoding='utf-8').replace('0.3.5',args.version)+'\n\n## Keyboard and Windows launch\nEnter sends your message; Shift+Enter adds a new line. Connect to a provider first. If Cursor is not installed yet, open Install-CursorConnection.cmd from File Explorer. This installs the official pinned CLI for your Windows account. Run it outside an agent sandbox so the desktop app can access it.\n',encoding='utf-8')
if tuple(map(int,args.version.split('.'))) >= (0,3,7):
    shutil.copyfile(repo/'docs/desktop/PROJECT-FOLDERS-037.md',release/'PROJECT-FOLDERS.md')
    with (release/'START-HERE.md').open('a',encoding='utf-8') as stream:
        stream.write('\n## Edit a project with Cursor / Grok\nUse File â†’ Open Folder. Select a project, choose a Cursor model, and ask for a change. Cursor edits files directly; completed tool cards show before/after changes. See PROJECT-FOLDERS.md for access and review details.\n')
if tuple(map(int,args.version.split('.'))) >= (0,3,8):
    shutil.copyfile(repo/'docs/desktop/AGENT-ACTIVITY-038.md',release/'AGENT-ACTIVITY.md')
    with (release/'START-HERE.md').open('a',encoding='utf-8') as stream:
        stream.write('\n## Recents and agent activity\nNew chat keeps earlier conversations in Recents. Each conversation has its own provider, model, folder and running agent. See AGENT-ACTIVITY.md for saved history, background work and restart behavior.\n')
if tuple(map(int,args.version.split('.'))) >= (0,3,9):
    current_composer = tuple(map(int,args.version.split('.'))) >= (0,3,10)
    shutil.copyfile(repo/('docs/desktop/CHAT-COMPOSER-0310.md' if current_composer else 'docs/desktop/AGENT-PERMISSIONS-039.md'),release/'AGENT-PERMISSIONS.md')
    with (release/'START-HERE.md').open('a',encoding='utf-8') as stream:
        stream.write('\n## Agent modes and permissions\n' + ('The message box contains the model picker and one Always ask / Always allow / Plan menu. Provider, sign-in and folder controls are under Connection at the top right. ' if current_composer else 'In a Cursor project chat, choose Agent or Plan and Always ask or Always allow requests below the model picker. ') + 'Choices are saved for that conversation. New chats start with Always ask. See AGENT-PERMISSIONS.md.\n')
if tuple(map(int,args.version.split('.'))) >= (0,3,12):
    shutil.copyfile(repo/'docs/desktop/FMP-CHARTING-0312.md',release/'FMP-CHARTING.md')
    with (release/'START-HERE.md').open('a',encoding='utf-8') as stream:
        stream.write('\n## FMP charts\nOpen Charting from the sidebar, enter a ticker and press Enter. Use Connection for your own FMP key. See FMP-CHARTING.md for intervals, snapshot behavior and requirements.\n')
if tuple(map(int,args.version.split('.'))) >= (0,3,13):
    shutil.copyfile(repo/'docs/desktop/CHAT-ATTACHMENTS-0313.md',release/'CHAT-ATTACHMENTS.md')
    shutil.copyfile(repo/'docs/desktop/PDFPIG-NOTICE.txt',release/'PDFPIG-NOTICE.txt')
    with (release/'START-HERE.md').open('a',encoding='utf-8') as stream:
        stream.write('\n## Chat attachments\nUse + beside the message box, paste an image, or drop files onto a chat. Supported documents send extracted text; image-capable connections receive images. Video/audio are retained for playback, with no model analysis yet. See CHAT-ATTACHMENTS.md for limits and provider support.\n')
if tuple(map(int,args.version.split('.'))) >= (0,3,14):
    shutil.copyfile(repo/'docs/desktop/SYSTEM-NAVIGATION-0314.md',release/'SYSTEM-NAVIGATION.md')
    with (release/'START-HERE.md').open('a',encoding='utf-8') as stream:
        stream.write('\n## One sidebar entry per system\nTools contains Capabilities and Options workbench. Later contains the offline market demo, with its own internal sections. Chat keeps New conversation inside the chat area, and Recents remain available. See SYSTEM-NAVIGATION.md.\n')
if tuple(map(int,args.version.split('.'))) >= (0,3,15):
    (release/'ACCOUNTS.md').write_text((repo/'docs/desktop/ACCOUNTS-0315.md').read_text(encoding='utf-8').replace('0.3.15',args.version),encoding='utf-8')
    old=(release/'START-HERE.md').read_text(encoding='utf-8')
    (release/'START-HERE.md').write_text('# xAI Desk '+args.version+' — partner beta\n\nExtract the entire ZIP first. Run Install.cmd, then Launch.cmd or the xAI Desk shortcut.\n\nSign in with browser using your own verified email account. See ACCOUNTS.md for Shared room setup and test limits. No account session, personal chats, provider credentials or API keys are included.\n\n'+old,encoding='utf-8')
if tuple(map(int,args.version.split('.'))) >= (0,3,19):
    if not (release/'payload/updater/SovereignAI.Updater.exe').is_file(): raise SystemExit('Updater helper missing; publish Sovereign.Desktop.Updater into app/updater first')
    shutil.copyfile(repo/'docs/desktop/IN-APP-UPDATES.md',release/'UPDATES.md')
files=[]
for path in sorted(release.rglob('*')):
    if path.is_file():
        if path.name.lower() in {'.env','auth.json','credentials.json','transcript.json'} or path.suffix.lower() in {'.db','.sqlite'}:
            raise SystemExit('Unexpected user state: '+str(path))
        with path.open('rb') as stream: digest=hashlib.file_digest(stream,'sha256').hexdigest()
        files.append(dict(path=str(path.relative_to(release)),sha256=digest,bytes=path.stat().st_size))
(release/'files.json').write_text(json.dumps(files,indent=2),encoding='utf-8')
print(json.dumps(dict(package=str(release),files=len(files))))
