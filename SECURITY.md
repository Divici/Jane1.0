# Security

Jane runs with a global low-level keyboard hook, synthesises keyboard input into other
applications, reads the contents of focused windows through UI Automation, and — when signed —
holds `uiAccess`. That is a meaningful amount of privilege on a desktop, so security reports are
welcome.

## Reporting a vulnerability

Please use GitHub's **[private vulnerability reporting](https://github.com/Divici/Jane1.0/security/advisories/new)**
rather than opening a public issue.

This is a personal project maintained by one person in their spare time. There is no SLA. Expect a
first response within a couple of weeks, and please do not assume silence is dismissal — chase it.

## Scope

Things that are in scope and that I would want to know about:

- A way to make Jane inject text into a window other than the one captured at key-down.
- A way to defeat the Deep Context blocklist and get password fields, banking pages or health
  content into a language-model prompt.
- A way to make the model downloader accept a payload that fails its pinned SHA-256, or to write
  outside `%LOCALAPPDATA%\Jane\models`.
- Anything that turns the `uiAccess` build into a privilege-escalation primitive.
- Prompt injection from screen content that changes what Jane does, rather than only what it types.

## Known and accepted

These are documented tradeoffs, not vulnerabilities. A report about one of them is still welcome if
it shows a consequence beyond what is described here.

- **`build/sign-uiaccess.ps1` installs a self-signed root certificate.** This is a genuine
  machine-wide trust change, and it is why the script constrains the certificate's EKU to code
  signing, exports the private key for offline storage, and deletes it from the store afterwards.
  It is appropriate for a machine you own and for nothing else. **Do not install a certificate
  someone else generated, and do not distribute binaries signed this way.**
- **Transcript history is stored unencrypted** in SQLite at `%LOCALAPPDATA%\Jane\jane.db`, readable
  by anything running as that user. Audio is never written to disk.
- **The blocklist is deliberately trigger-happy** and its finance and health word lists are
  hand-written, which makes them definitionally incomplete. A false negative there means context
  that should not have been read; the layered design exists to make that unlikely, not impossible.
- **Jane types into whatever has focus.** If focus changes between key-down and injection the
  dictation is aborted, but a target that changes what it is *while keeping the same window and
  process identity* is not something Jane can detect.

## Out of scope

- An attacker who already has code execution as your user. They can read the database, hook the
  keyboard themselves, and do everything Jane does.
- The behaviour of the third-party models, which are downloaded unmodified from the sources listed
  in [NOTICE.md](NOTICE.md).
