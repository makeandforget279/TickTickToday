# Security policy

## Supported versions

Security fixes are applied to the latest version on the default branch.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability. Use the repository
host's private security advisory feature when available, or contact the
maintainer through a private channel listed on the maintainer's profile.

Include:

- A concise description and potential impact.
- Reproduction steps or a minimal proof of concept.
- The affected commit or version.
- Any suggested mitigation.

Do not include real TickTick credentials, OAuth tokens, task data, or other
personal information. Replace them with clearly marked test values.

## Secret exposure

If a TickTick access token, client secret, or OAuth cache is exposed, revoke or
rotate it immediately. Deleting the current file does not remove a secret from
existing Git history or forks.
