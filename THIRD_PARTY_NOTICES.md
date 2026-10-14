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
- cloudflared is **not bundled** in this repository. You download it separately from Cloudflare's releases page (see README). It is covered by its own Apache-2.0 license and any NOTICE file Cloudflare ships with it, at https://github.com/cloudflare/cloudflared/blob/master/LICENSE.

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

