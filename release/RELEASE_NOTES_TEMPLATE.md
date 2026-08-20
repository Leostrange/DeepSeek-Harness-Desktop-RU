# DeepSeek Harness Desktop RU — Windows release

## What's included

- Windows desktop client for DeepSeek Harness
- Automatic connection to the local Harness endpoint
- Built-in Russian UI localization
- Windows installer
- Portable/distribution archives

## Downloads

- `DeepSeekHarness-Setup.exe` — recommended installer
- `DeepSeekHarness-Distribution.zip` — portable distribution
- `DeepSeekHarness-Native.zip` — native client package
- `DeepSeekHarness-CodeSigning.cer` — public self-signed certificate
- `SHA256SUMS.txt` — SHA-256 checksums

## Verify downloads

Run in PowerShell:

```powershell
Get-FileHash .\DeepSeekHarness-Setup.exe -Algorithm SHA256
```

Compare the result with `SHA256SUMS.txt`.

> The supplied certificate is self-signed and is not equivalent to a publicly trusted commercial code-signing certificate.

## Upstream

DeepSeek Harness: https://github.com/deepseek-ai/deepseek-harness

This is an independent community project and is not affiliated with or endorsed by DeepSeek.
