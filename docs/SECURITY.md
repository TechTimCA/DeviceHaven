# DeviceHaven security design

DeviceHaven's agent has full control of every PC it's installed on, so the
whole design starts from one rule: **only your server can tell your PCs what
to do, and your data never leaves your network.**

## Built in v0.1

| Area | How it works |
|---|---|
| **No cloud** | The server and its database run on your own machine. Nothing is sent to any outside service. |
| **Encrypted connections** | HTTPS only, with TLS 1.2 or 1.3. The server makes its own ECDSA P-256 certificate on first start, valid for five years. |
| **Certificate pinning** | Each agent is installed with the server's SHA-256 fingerprint and refuses any other certificate. That stops a fake server or a man-in-the-middle, even on a compromised network. Tested in `AgentEndToEndTests`. |
| **Outbound only** | Agents connect out to the server. PCs never listen for incoming connections. |
| **Enrolment codes** | 80-bit random codes, single use, expiring after 1–168 hours (24 by default). Using a code is a single atomic database update, so two PCs can't both use the same code. Every bad code gets the same answer, so codes can't be probed. Rate-limited per IP address. |
| **Device tokens** | 256-bit random, issued once at enrolment. The server stores only a SHA-256 hash and compares in constant time. A revoked device is refused at once. |
| **Secrets at rest (server)** | Codes and tokens are stored only as hashes. The certificate's password is encrypted with ASP.NET Data Protection, whose keys are protected by Windows DPAPI (machine scope). |
| **Secrets at rest (agent)** | The device token is encrypted with Windows DPAPI (machine scope), so the file is useless if copied to another PC. The enrolment code is deleted after use. |
| **Installers** | Both MSIs lock their data folders to SYSTEM and Administrators. The agent MSI passes its settings through a registry key only SYSTEM and Administrators can read, and the agent deletes it after use. `ENROLLCODE` is marked hidden so it never appears in install logs. The firewall rule is limited to the server program and port, on domain and private networks only. |
| **Local trust** | The server adds its own certificate (public part only) to the server PC's trusted list, so the console opens without a warning. It isn't a certificate authority, so it can't vouch for anything else. |
| **Admin API** | Until sign-in with two-step verification is built, it only answers requests from the server itself (loopback). |
| **Audit log** | Every admin and enrolment action is recorded. Database triggers block any change to or deletion of the log. |
| **Input limits** | Request size limits, length limits on stored text, and parameterised SQL throughout. |
| **Response headers** | `nosniff`, `X-Frame-Options: DENY`, a strict Content-Security-Policy, HSTS and `no-referrer`. |

## Planned

- Running the server under its own low-privilege service account instead of LocalSystem.
- Admin sign-in with two-step verification (TOTP) and roles (view-only, full admin).
- Mutual TLS: each agent gets its own client certificate at enrolment, on top of the token.
- Signed commands: every job the server sends is signed, and agents verify it before running it.
- Encrypting the database file itself (SQLCipher), not just the secrets inside it.
- Code-signed installers and binaries.
- Signed self-updates for both the server and the agents.
- An outside security review before the first public release.

## Reporting a problem

Please don't open a public issue for security problems. Contact the maintainer
privately through GitHub instead.
