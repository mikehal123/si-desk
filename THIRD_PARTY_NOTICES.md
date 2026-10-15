# Third-Party Notices

SI Desk uses or was inspired by the following third-party work.

## RongleCat/grok-app

- Source: https://github.com/RongleCat/grok-app
- License: MIT
- Use: Its ACP client patterns inspired `GrokBuildAcp.cs` and `GrokBuildChatClient.cs`.

```
MIT License

Copyright (c) 2026 RongleCat

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## cloudflared (Cloudflare)

- Source: https://github.com/cloudflare/cloudflared
- License: Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0)
- Use: Shared room invites run through a cloudflared tunnel.
- Published releases include the pinned Windows amd64 binary cloudflared 2026.9.1 (`cloudflared-windows-amd64.exe`, SHA-256 `2837888cc0f5d58f15b6dc478376de90b4d3ba5241c7947455d1e0a0df429712`). The git repository does not commit that exe. `scripts/Get-Cloudflared.ps1` downloads it for Release builds. The Apache-2.0 text shipped beside the binary is `apps/desktop/room-tunnel/LICENSE.txt`. The pin is recorded in `apps/desktop/room-tunnel/NOTICE.txt`. Upstream license: https://github.com/cloudflare/cloudflared/blob/master/LICENSE.

## PdfPig

- Source: https://github.com/UglyToad/PdfPig
- Package: PdfPig 0.1.16
- License: Apache-2.0 (https://www.apache.org/licenses/LICENSE-2.0)
- Use: `ChatAttachments` reads text out of PDF files attached to a chat.

## Windows App SDK

- Source: https://github.com/microsoft/WindowsAppSDK
- Package: Microsoft.WindowsAppSDK 2.4.0
- License: Microsoft Software License Terms for Microsoft Windows App SDK (`license.txt` in the NuGet package). Those terms are not Apache-2.0 or MIT.
- Use: the desktop shell is a WinUI app and publishes the Windows App SDK self-contained.

