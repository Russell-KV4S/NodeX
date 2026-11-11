# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Use GitHub's private reporting instead:
[Report a vulnerability](https://github.com/Russell-KV4S/NodeX/security/advisories/new).

Include what you found, how to reproduce it, and the version. You can expect an acknowledgement within about a week. This is a hobby project run by one person, so fixes are best-effort.

## What matters most

- The node password must never appear in settings files, logs, status text or on the wire in clear form.
- Nothing remote or on the car screen should be able to key a transmitter without the user's explicit action.
- Only the dialled endpoint may send to a call socket.

## Supported versions

Only the latest release is supported.
