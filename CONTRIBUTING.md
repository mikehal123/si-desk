# Contributing to SI Desk

Thanks for helping out! SI Desk is an unofficial, community-built Windows app for Grok and is in public beta, so bug reports and small focused PRs are very welcome.

## What you need
- Windows 10 or 11 (x64)
- .NET 10 SDK (`global.json` asks for 10.0.400 or a later 10.0 feature band)

## Build
From the repo root:

```
dotnet build apps/desktop/Sovereign.Desktop -c Debug -p:Platform=x64
```

Warnings are treated as errors (`Directory.Build.props`), so keep builds warning-free.

## Test
```
dotnet test tests/desktop/XaiDesk.Tests
dotnet test tests/desktop/Sovereign.Desktop.Tests
```

## Before you open a PR
1. Build and tests pass.
2. Run the public scan and make sure it reports **`hits: 0`**:
   ```
   powershell -ExecutionPolicy Bypass -File scripts/public-scan.ps1
   ```
   It checks for secrets, personal paths, email addresses, private IPs and leftover private markers. A PR with hits won't be merged.
3. **Never commit secrets** — no API keys, tokens, `.env` files, personal paths or logs with account data. Use placeholders in examples.
4. Keep PRs small and explain what changed and why. Screenshots help for UI changes.

## Release builds
Maintainers build releases with `scripts/publish-release.ps1`, which downloads the pinned cloudflared, checks its SHA-256, and runs the public scan. Tagging `v*` runs the same script in GitHub Actions. Debug builds warn if `apps/desktop/room-tunnel/cloudflared.exe` is missing; Release builds fail.

## License
By contributing you agree your work is released under the MIT License (see `LICENSE`).
