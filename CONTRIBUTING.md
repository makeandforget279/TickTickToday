# Contributing to TickTickToday

Thank you for helping improve TickTickToday.

## Before opening an issue

- Search existing issues for the same behavior.
- Remove tokens, client secrets, task titles, list names, and other personal
  information from logs or screenshots.
- Include the operating system, .NET SDK version, command used, expected
  behavior, and actual behavior.

Security vulnerabilities should not be reported in public issues. Follow
[SECURITY.md](SECURITY.md) instead.

## Development workflow

1. Fork the repository and create a focused branch.
2. Keep identifiers, user-facing text, comments, documentation, and file names
   in English.
3. Do not commit `.env`, `.ticktick-token.json`, `periodic-tasks.xml`, build
   output, or personal TickTick data.
4. Build and validate the project:

   ```powershell
   dotnet restore
   dotnet build --configuration Release
   ```

   To validate configuration without contacting TickTick, set
   `check-config=1` in `ticktick-today.local.ini`, then run
   `dotnet run --project .\TickTickToday.csproj`.

5. Describe the reason for the change and how it was verified in the pull
   request.

Prefer small pull requests with one clear purpose. Update README examples when
behavior or configuration changes.
