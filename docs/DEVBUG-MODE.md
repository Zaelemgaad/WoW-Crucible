# Devbug Mode

Devbug Mode is Crucible's opt-in, high-detail development telemetry. It diagnoses editor, MPQ, DBC, model-preview, database, and asset-library workflows without making normal use slower or filling the drive with permanent logs.

## Normal mode

- Does not create routine startup, success, status, or activity logs.
- Handled errors remain visible in the UI but do not create log files.
- A genuinely unhandled or fatal process failure attempts to write a crash record under `Logs\Crashes`.
- Only the newest three crash sessions are retained.

## Devbug Mode

Enable **DEVBUG ON** in the desktop header. The choice persists in `Settings\desktop.json`. For one diagnostic launch without changing the saved choice, use:

```powershell
WoWCrucible.Desktop-latest.exe --devbug
```

For any CLI command, put `--devbug` anywhere on the command line. The clearest form is before the command group:

```powershell
.\wowcrucible.exe --devbug mpq list "G:\patch-H.MPQ"
.\wowcrucible.exe --devbug dbc validate schema.xml dbc-folder --recursive
```

The CLI continues printing normally and mirrors its output, errors, sanitized arguments, duration, exit code, and full unexpected exception details into `Logs\Debug\WoWCrucible-CLI-Devbug-*.log`. It does not open another terminal because the CLI already runs inside one. The newest three CLI Devbug sessions are retained independently from desktop sessions. Database password environment-variable values are never read into the log.

Crucible opens a live terminal and writes the same structured events to `Logs\Debug\WoWCrucible-Devbug-<timestamp>-p<process>.log`. Events contain:

- millisecond timestamp and local UTC offset;
- monotonically increasing session sequence;
- severity, managed thread, category, and action;
- named fields such as path, row count, duration, result count, and selected mode;
- full exception type, message, and stack trace for failures.

Logging uses a below-normal-priority background writer and a persistent file stream. Normal operations only pay for a disabled-mode branch. The newest three Devbug session logs are retained; creating a fourth deletes only the oldest matching Devbug log.

The terminal close command is disabled so closing the diagnostic console cannot accidentally terminate Crucible. Turn Devbug Mode off from the application to close it safely.

## Portable application data

Crucible-owned persistent files use organized folders beside the executable:

```text
WoWCrucible.Desktop-latest.exe
Settings\
  desktop.json
  settings.json
Profiles\
Logs\
  Crashes\
  Debug\
docs\
```

Folders are created only when needed. If Windows denies writes beside the executable, Crucible falls back to `%LOCALAPPDATA%\WoWCrucible`; every Devbug session header reports the effective data root and whether portable mode is active.

The self-contained single-file executable embeds managed dependencies. Windows and .NET may extract native runtime components to an OS-managed temporary location while the program runs; those temporary runtime mechanics are not Crucible-owned persistent data.

## Sensitive values

Devbug logs record operational paths, IDs, counts, timings, UI actions, and failures. Database passwords are deliberately excluded. Review a session before sharing it publicly because local paths, account names embedded in paths, database hostnames, and project names may still be visible.
