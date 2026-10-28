# Taskus

> **Warning: work in progress.** Taskus is under active development and is not ready for production use.
> Task file format, command-line options and behaviour may change in any release without notice.
> The SMTP transport has not yet been tested against a real server.

A console tool for declarative data-transfer tasks: take data from a source (Excel files, a PostgreSQL or ODBC query,
an HTTP API), convert each row, and put it into a sink (a database table, an Excel file, e-mail, an HTTP API).

Each task is a YAML file. Taskus runs it manually or from Windows Task Scheduler: it checks the input,
processes what it finds, reports the result, and exits.

Status: version 0.5.1, second development iteration. Sources: `excel`, `postgres`, `odbc`, `http`. Sinks: `postgres`, `odbc`, `excel`, `smtp`, `http`.
HTTP calls support retries and a rate limit.
Design and task file reference: [Docs/design.md](Docs/design.md).

## Usage

```
taskus run tasks/import-clients.yaml          # one task
taskus run tasks/                             # every *.yaml / *.yml in a folder
taskus run tasks/import-clients.yaml --dry-run
taskus validate tasks/
taskus run tasks/import-clients.yaml --log-level=trace   # every row and HTTP call in the log files
taskus schema                                 # JSON Schema of task files
```

Exit codes: `0` success, `1` the run had errors, `2` a task file is invalid.

Example tasks are in [examples/](examples). Copy `examples/secrets.yaml.example` to `secrets.yaml` next to
`taskus.exe` and fill in the connection strings and SMTP settings.

Editor support: start a task file with the line below. VS Code (with the Red Hat YAML extension) and JetBrains
IDEs then complete settings and mark unknown or missing ones.

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/vdasus/taskus/main/schema/task.schema.json
```

## Templates

Mapping expressions, URLs, file names and e-mail bodies in task files use [Scriban](https://scriban.github.io/),
a template language for .NET:

- [Language reference](https://scriban.github.io/docs/language): `{{ ... }}` blocks, variables, operators,
  conditions, loops, pipes (`|`).
- [Built-in functions](https://scriban.github.io/docs/builtins): `string.*`, `math.*`, `date.*`, `html.*` and
  others.

```yaml
map:
  code:    "{{ row.Code | string.strip }}"                    # source field, trimmed
  name:    "{{ row['Client name'] }}"                         # field name with a space
  limit:   "{{ row.Limit ?? 0 }}"                             # default for an empty value
  subject: "Invoice {{ row.Id }}"                             # text with a value inside
  file:    "{{ date.now | date.to_string '%Y%m%d' }}.xlsx"    # current date
```

Taskus-specific rules (which variables exist, when a value keeps its type) are in
[Docs/design.md](Docs/design.md#mapping-and-templates).

## Download

Windows builds are attached to [GitHub releases](https://github.com/vdasus/taskus/releases) in two kinds:
`self-contained` needs nothing installed; `framework-dependent` is much smaller but needs the
[.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0). Unpack the zip,
copy `examples/secrets.yaml.example` to `secrets.yaml` next to `taskus.exe`, and adjust the example tasks.
Versions follow [semantic versioning](https://semver.org); while the version is `0.x`, any release may break
task files.

## Build

```
dotnet test
dotnet publish src/Taskus -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
dotnet publish src/Taskus -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/win-x64-fdd
```

The result is `taskus.exe` and `appsettings.json` (logging settings); the machine that runs it needs no .NET
installation. Logs go to `logs` next to the executable: a text file and a JSON file per run.

## Release

CI runs the tests on Linux and Windows for every push to `main` and every pull request. To release, push a
version tag:

```
git tag -a v0.2.0 -m "Taskus 0.2.0"
git push origin v0.2.0
```

The release workflow tests, builds both Windows zips (self-contained and framework-dependent) with the tag's
version, and publishes a GitHub release with the zips and their SHA-256 checksums. `0.x` versions are marked as pre-release.

## Disclaimer

This software is provided "as is", without warranty of any kind. Use it at your own risk. The author accepts no
liability for any damage, data loss or other consequences of its use.

You may use, copy, modify and distribute it for any purpose, commercial or not, under the terms of the
[MIT License](LICENSE).
