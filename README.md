# Taskus

> **Warning: work in progress.** Taskus is under active development and is not ready for production use.
> Task file format, command-line options and behaviour may change in any release without notice.
> The PostgreSQL and SMTP transports have not yet been tested against real servers.

A console tool for declarative data-transfer tasks: take data from a source (Excel files, a PostgreSQL query,
an HTTP API), convert each row, and put it into a sink (a database table, an Excel file, e-mail, an HTTP API).

Each task is a YAML file. Taskus runs it manually or from Windows Task Scheduler: it checks the input,
processes what it finds, reports the result, and exits.

Status: version 0.1.0, second development iteration. Sources: `excel`, `postgres`, `http`. Sinks: `postgres`, `excel`, `smtp`, `http`.
HTTP calls support retries and a rate limit.
Design and task file reference: [Docs/design.md](Docs/design.md).

## Usage

```
taskus run tasks/import-clients.yaml          # one task
taskus run tasks/                             # every *.yaml / *.yml in a folder
taskus run tasks/import-clients.yaml --dry-run
taskus validate tasks/
```

Exit codes: `0` success, `1` the run had errors, `2` a task file is invalid.

Example tasks are in [examples/](examples). Copy `examples/secrets.yaml.example` to `secrets.yaml` next to
`taskus.exe` and fill in the connection strings and SMTP settings.

## Download

Windows builds are attached to [GitHub releases](https://github.com/vdasus/taskus/releases): unpack the zip,
copy `examples/secrets.yaml.example` to `secrets.yaml` next to `taskus.exe`, and adjust the example tasks.
Versions follow [semantic versioning](https://semver.org); while the version is `0.x`, any release may break
task files.

## Build

```
dotnet test
dotnet publish src/Taskus -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
```

The result is `taskus.exe` and `appsettings.json` (logging settings); the machine that runs it needs no .NET
installation. Logs go to `logs` next to the executable: a text file and a JSON file per run.

## Disclaimer

This software is provided "as is", without warranty of any kind. Use it at your own risk. The author accepts no
liability for any damage, data loss or other consequences of its use.

You may use, copy, modify and distribute it for any purpose, commercial or not, under the terms of the
[MIT License](LICENSE).
