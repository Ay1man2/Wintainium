# Security

Wintainium downloads and starts programs, so security problems matter even in a personal project.

## Reporting a vulnerability

Please report it privately, not in a public issue:

1. Open the repository's **Security** tab.
2. Click **Report a vulnerability**.
3. Describe the problem, the version or commit, and the steps to reproduce it.

Only you and the maintainer can see the report. Please give a reasonable time for a fix before sharing details publicly.

This is a personal project shared as-is, so there's no promised response time and no bounty. Reports are still read and taken seriously.

## In scope

- The Wintainium source in `src/` and `known-apps.json`
- `Wintainium.exe` and how it installs, updates and uninstalls itself
- The release files: `Wintainium.exe`, the portable zip and their `.sha256` files

Some examples: anything that lets a release server, a downloaded file or a file placed next to Wintainium run code, write outside the folders it should, or reach your GitHub token.

## Out of scope

- Problems in the apps Wintainium tracks, or on their release sites. Report those to their own projects.
- Anything that needs someone already able to run code as you, or to edit your own `wintainium-apps.json`. That file is yours and Wintainium does what it says.

## Supported versions

Only the latest release gets fixes.
