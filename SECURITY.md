# Security

humcp runs programs and connects to addresses that its user configures, and shows text, images and
files that third-party servers send. It treats everything a server sends as untrusted: text is
sanitised before it reaches the screen or a speech synthesiser, file types are decided by humcp
rather than by the server, only web links are ever opened, and nothing a server sends is executed.

If you find a way around any of that, or any other security problem, please report it privately
rather than in a public issue: use GitHub's
[private vulnerability reporting](https://github.com/fastfinge/humcp/security/advisories/new).

Fixes ship as 1.x releases. Only the latest release is supported.
