# Security

## API keys and credentials

DeepSeek Harness Desktop RU does not require API keys to be embedded in the launcher or installer. Configure providers through the normal DeepSeek Harness interface.

Do not attach the contents of `%APPDATA%\DeepSeekHarness\data` to public issues unless you have reviewed and removed credentials and other sensitive information.

## Code signing

The current project certificate is self-signed. A self-signed certificate can be used to verify consistency after explicit trust is established, but it does not provide the Windows reputation or publisher identity of a certificate issued by a publicly trusted code-signing CA.

## Reporting a security issue

Please avoid publishing exploitable details or secrets in a public issue. Contact the repository owner privately through their GitHub profile when responsible disclosure is appropriate.
