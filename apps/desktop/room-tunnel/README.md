# Room tunnel (cloudflared)

Online shared rooms start `room-tunnel\cloudflared.exe` beside `SIDesk.exe`. The app checks the file's SHA-256 before it launches the process.

Pinned binary:

| | |
| --- | --- |
| Project | [cloudflare/cloudflared](https://github.com/cloudflare/cloudflared) |
| Release | 2026.9.1 |
| Asset | `cloudflared-windows-amd64.exe` |
| SHA-256 | `2837888cc0f5d58f15b6dc478376de90b4d3ba5241c7947455d1e0a0df429712` |

The same pin is in `cloudflared.pin` and `NOTICE.txt`. `RoomTunnel.cs` repeats the SHA-256 and refuses any other file.

`scripts/Get-Cloudflared.ps1` downloads that asset into this folder and deletes the download if the hash does not match. `scripts/publish-release.ps1` and the release workflow both run that script. The exe is listed in `.gitignore` and must not be committed.

A Release build fails while `cloudflared.exe` is missing. A Debug build warns and still compiles. If an installed copy is missing the file, Create online room reports: "Online rooms need the room tunnel (cloudflared.exe), which is missing from this install. Reinstall or update SI Desk." A hash mismatch still reports: "Room tunnel hash mismatch. Repair the app."

cloudflared is Apache-2.0. See `LICENSE.txt` and `NOTICE.txt`.
