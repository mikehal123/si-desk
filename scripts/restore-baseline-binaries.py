from pathlib import Path
import sys,json,hashlib,zipfile
root=Path(__file__).resolve().parents[1]
archive=Path(sys.argv[1])
with archive.open("rb") as f:
    if hashlib.file_digest(f,"sha256").hexdigest()!="88854b67b816c62344a2efa1247bf21306ad5c467e4b94d6e14071584292ada8":raise ValueError("Wrong baseline archive")
with zipfile.ZipFile(archive) as z:
    for row in json.loads((root/"docs/desktop/portable-desk-baseline/binary-inputs.json").read_text()):
        name=row["path"]
        if not name.startswith("apps/desktop/"):raise ValueError("Unexpected binary source")
        raw=z.read("xAIDesk-Windows-0.3.27/payload/"+name.removeprefix("apps/desktop/"))
        if len(raw)!=row["bytes"] or hashlib.sha256(raw).hexdigest()!=row["sha256"]:raise ValueError("Binary mismatch")
        dest=root/name;dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(raw)
print("Pinned vendor binaries restored")
