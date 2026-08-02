# TickTickToday

TickTickToday is a .NET console application that finds incomplete TickTick
tasks, completes selected expired tasks, and reschedules overdue work into the
next available time slots.

The project is community-maintained and is not affiliated with or endorsed by
TickTick.

## Features

- Lists incomplete tasks by due date.
- Reschedules overdue tasks while preserving their duration.
- Avoids occupied time from existing and recurring TickTick tasks.
- Reserves time from a local cron-based periodic schedule.
- Optionally creates scheduled periodic tasks in TickTick.
- Supports safe dry runs before any remote changes are made.
- Uses `#donotmove` and `#closewhenold` tags for per-task control.
- Handles daylight-saving changes in the `Europe/Bucharest` time zone.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A TickTick account
- Either a TickTick access token or OAuth application credentials

OAuth credentials can be created in the
[TickTick Developer Center](https://developer.ticktick.com/manage).

## Quick start

1. Create the local files from their public examples:

   ```powershell
   Copy-Item .env.example .env
   Copy-Item ticktick-today.ini ticktick-today.local.ini
   Copy-Item periodic-tasks.example.xml periodic-tasks.xml
   ```

2. Add your credentials to `.env`. Use either a direct access token:

   ```dotenv
   TICKTICK_ACCESS_TOKEN=your-access-token
   ```

   or OAuth application credentials:

   ```dotenv
   TICKTICK_CLIENT_ID=your-client-id
   TICKTICK_CLIENT_SECRET=your-client-secret
   ```

3. Set `check-config=1` in `ticktick-today.local.ini`, then validate without
   contacting TickTick:

   ```powershell
   dotnet run --project .\TickTickToday.csproj -- .\ticktick-today.local.ini
   ```

4. Set `check-config=0` and `simulate=1`, then preview all actions:

   ```powershell
   dotnet run --project .\TickTickToday.csproj -- .\ticktick-today.local.ini
   ```

5. When the preview is correct, set `simulate=0` and run normally:

   ```powershell
   dotnet run --project .\TickTickToday.csproj -- .\ticktick-today.local.ini
   ```

The application requires exactly one INI file path and does not select a
configuration on its own. On Windows, `run-ticktick-today.bat` uses the safe
public `ticktick-today.ini` by default, accepts a different INI path as its first
argument, and keeps the terminal open for review. Use
`run-ticktick-today-local.bat` to start with `ticktick-today.local.ini` instead.

## Authentication

Runtime environment variables take precedence over `.env`. The application
recognizes:

| Variable | Purpose |
| --- | --- |
| `TICKTICK_ACCESS_TOKEN` | Uses a direct access token and skips OAuth. |
| `TICKTICK_CLIENT_ID` | OAuth application client ID. |
| `TICKTICK_CLIENT_SECRET` | OAuth application client secret. |
| `TICKTICK_REDIRECT_URI` | Optional override for the configured redirect URI. |

For OAuth, configure `http://127.0.0.1:8089/` as the redirect URL in the
TickTick Developer Center. On the first run, TickTickToday opens the
authorization page and temporarily listens for the local redirect. If the
listener cannot start, the application asks for the redirect URL or OAuth
`code` manually.

The resulting token is cached in `.ticktick-token.json`. Set `login=1` in the
INI file to ignore the cache and authorize again.

## Configuration

The tracked `ticktick-today.ini` file documents every setting and defaults to
simulation mode. Keep personal overrides in the ignored
`ticktick-today.local.ini` file:

| Setting | Meaning |
| --- | --- |
| `redirect-uri` | OAuth redirect URL. Defaults to `http://127.0.0.1:8089/`. |
| `periodic-tasks-file` | Path to the private periodic schedule XML. |
| `move` | Maximum tasks moved per run. Use `unlimited` for no limit or `0` to list only. |
| `plan-days` | Maximum number of future days searched for a free slot. |
| `lookback-days` | Maximum number of past days searched for incomplete tasks. |
| `day-start` | Earliest start time for a rescheduled task, in `HH:mm` format. |
| `day-end` | Latest end time for a rescheduled task, in `HH:mm` format; must be later than `day-start`. |
| `periodic-days` | Number of days populated with periodic tasks; `0` disables creation. |
| `close-when-old` | Completes expired tasks carrying `#closewhenold`. |
| `login` | Forces OAuth authorization. |
| `simulate` | Calculates actions without updating TickTick. |
| `check-config` | Validates the INI and periodic schedule, then exits without contacting TickTick. |

Boolean settings accept `1/0`, `true/false`, and `yes/no`.
All runtime behavior is controlled by the selected INI file. There are no
per-setting command-line overrides.

## Periodic tasks

`periodic-tasks.xml` is intentionally ignored by Git because schedules often
contain personal information. Copy `periodic-tasks.example.xml` to create a
local schedule.

Each rule uses a five-field Unix cron expression:

```xml
<periodicTasks>
  <task
    cron="0 9 * * 1-5"
    duration="00:30"
    title="Daily planning"
    list="Work"
    id="DAILY-PLANNING" />
</periodicTasks>
```

The cron fields are `minute hour day-of-month month day-of-week`. Standard
wildcards, lists, ranges, steps, `SUN-SAT`, `JAN-DEC`, and the extensions
supported by Cronos are accepted. Day `0` or `7` is Sunday. When both
day-of-month and day-of-week are restricted, both must match.

`duration` accepts `HH:mm` from `00:01` through `24:00`. `list` is optional and
defaults to Inbox. A leading icon or emoji may be omitted from a list name if
the remaining text identifies exactly one TickTick list. `id` is required,
must be unique, and may contain 3-32 letters, digits, hyphens, or underscores.

Generated tasks receive the description marker `Periodic rule ID: <id>` and
the `#closewhenold` tag. TickTickToday uses the identifier, time interval, and
list to prevent duplicate occurrences. The legacy `day`, `start`, and `end`
attributes remain supported with English day names, abbreviations, or numbers.

## Scheduling behavior

- Completed tasks are excluded.
- Today includes only tasks whose due time has already passed.
- Tasks are considered from yesterday backward, followed by tasks due today.
- Existing tasks are loaded once and filtered locally.
- Planning uses 30-minute increments between the configured `day-start` and
  `day-end` times in the Bucharest time zone.
- The original start time is preferred when it is available and unoccupied.
- Task duration, priority, tags, and content are preserved when dates change.
- `#donotmove` prevents a task from being rescheduled.
- `#closewhenold` allows an expired task to be completed automatically when
  `close-when-old=1`.
- A start-time reminder is set with `TRIGGER:PT0S` after a move.

## Security

Never commit `.env` or `.ticktick-token.json`. Both are covered by
`.gitignore`, along with legacy credential file names and build artifacts.
If a secret was committed before these safeguards existed, removing the file
is not enough: revoke or rotate the secret and purge it from Git history.

See [SECURITY.md](SECURITY.md) for private vulnerability reporting guidance.

## Development

```powershell
dotnet restore
dotnet build --configuration Release
dotnet run --project .\TickTickToday.csproj -- .\ticktick-today.local.ini
```

Set `check-config=1` in `ticktick-today.local.ini` for an offline configuration
check.

The TickTick Open API reference is available at
<https://developer.ticktick.com/docs#/openapi>.

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before
opening a pull request.

## License

TickTickToday is released under the [Zero-Clause BSD license](LICENSE). Anyone
may use, copy, modify, or distribute it for any purpose, with or without a fee.
The software is provided as-is, without warranties, and the author accepts no
liability for damages arising from its use or performance.
