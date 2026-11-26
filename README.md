# Taskus

> **Warning: work in progress.** Taskus is under active development and is not ready for production use.
> Task file format, command-line options and behaviour may change in any release without notice.
> The SMTP transport has not yet been tested against a real server.

A console tool for declarative data-transfer tasks: take data from a source (Excel files, a PostgreSQL or ODBC query,
an HTTP API), convert each row, and put it into a sink (a database table, an Excel file, e-mail, an HTTP API).

Each task is a YAML file. Taskus runs it manually or from Windows Task Scheduler: it checks the input,
processes what it finds, reports the result, and exits.

Status: version 0.8.0, second development iteration. Sources: `excel`, `postgres`, `odbc`, `http`. Sinks: `postgres`, `odbc`, `excel`, `smtp`, `http`.
HTTP calls support retries and a rate limit.
Design and task file reference: [Docs/design.md](Docs/design.md).

## Usage

```
taskus run tasks/import-clients.yaml          # one task
taskus run tasks/                             # every *.yaml / *.yml in a folder
taskus run tasks/import-clients.yaml --dry-run
taskus validate tasks/
taskus run tasks/import-clients.yaml --log-level=trace   # every row and HTTP call in the log files
taskus run tasks/ --logs=D:\logs --log-days=30           # log folder; delete logs older than 30 days (default 90)
taskus schema                                 # JSON Schema of task files
taskus --version                              # print the version, e.g. 0.8.0
```

Exit codes: `0` success, `1` the run had errors or the same task is still running, `2` a task file is invalid
or the run could not start. `taskus --help` lists every option.

Ctrl+C stops the run; a second Ctrl+C ends the process at once. What happens to a file being processed is
described in [Docs/design.md](Docs/design.md).

Example tasks are in [examples/](examples). Copy `examples/secrets.yaml.example` to `secrets.yaml` next to
`taskus.exe`, fill in the connection strings and SMTP settings, and encrypt it:

```
taskus secrets protect      # secrets.yaml -> secrets.sec, readable only on this machine; secrets.yaml is deleted
taskus secrets unprotect    # secrets.sec -> secrets.yaml for editing; protect again afterwards
```

On Windows `taskus run` reads only `secrets.sec` and stops with an error while `secrets.yaml` is present.
`--current-user` limits decryption to the account that ran `protect`. Details: [Docs/design.md](Docs/design.md#secrets).

Editor support: start a task file with the line below. VS Code (with the Red Hat YAML extension) and JetBrains
IDEs then complete settings and mark unknown or missing ones.

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/vdasus/taskus/main/schema/task.schema.json
```

## Taskus Studio

[Taskus Studio](https://github.com/vdasus/taskus-studio) is a cross-platform GUI editor for task files, built with
Avalonia. It uses the `Taskus.Core` library, so it loads and validates task files with the same rules as `taskus`.

Taskus and Taskus Studio share the major and minor version: Studio `X.Y.n` works with Taskus `X.Y.*`.

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

Builds for Windows x64, Linux x64 and macOS on Apple silicon are attached to
[GitHub releases](https://github.com/vdasus/taskus/releases) as `taskus-<version>-<rid>-<kind>.zip`, where `<rid>`
is `win-x64`, `linux-x64` or `osx-arm64`. Each zip has a `.sha256` checksum file. Pick the zip for your system and
one of two kinds: `self-contained` needs nothing installed; `framework-dependent` is much smaller but needs the
[.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0). A zip holds the single-file executable
(`taskus.exe` on Windows, `taskus` on Linux and macOS), `appsettings.json`, `README.md`, `LICENSE` and `examples/`.

Unpack the zip and copy `examples/secrets.yaml.example` to `secrets.yaml` next to the executable. On Windows, run
`taskus secrets protect` to encrypt it (DPAPI). Linux and macOS have no secrets protection: allow only the account
that runs Taskus to read the file (`chmod 600 secrets.yaml`). If the executable bit is lost on Linux or macOS
(some unzip tools drop it), run `chmod +x taskus`. The macOS build is not signed; remove the quarantine attribute
from the unpacked folder with `xattr -dr com.apple.quarantine <folder>`. Then adjust the example tasks.

Versions follow [semantic versioning](https://semver.org); while the version is `0.x`, any release may break
task files.

## Build

```
dotnet test
dotnet publish src/Taskus -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
dotnet publish src/Taskus -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/win-x64-fdd
```

The result is `taskus.exe` and `appsettings.json` (logging settings). For Linux or macOS use `-r linux-x64` or
`-r osx-arm64`; the executable is then `taskus`. The self-contained build needs no .NET
installation; the framework-dependent one needs the .NET 10 runtime. Logs go to `logs` next to the executable:
a text file and a JSON file per run, `taskus_<yyyyMMdd_HHmmss>_<process id>.*`. Files older than 90 days are
deleted at start (`--log-days`).

## Release

CI runs the tests on Linux and Windows for every push to `main` and every pull request. To release, set
`<Version>` in both `src/Taskus/Taskus.csproj` and `src/Taskus.Core/Taskus.Core.csproj` and the status line above,
commit and push, then push a version tag:

```
git tag -a v0.2.0 -m "Taskus 0.2.0"
git push origin v0.2.0
```

Do not create the release by hand (`gh release create`): the workflow creates it and fails when it already exists.

The release workflow tests, builds six zips with the tag's version (self-contained and framework-dependent for
each of `win-x64`, `linux-x64` and `osx-arm64`), packs the `Taskus.Core` NuGet package with the same version, and
publishes a GitHub release with 13 assets: the six zips, their six SHA-256 checksum files and the `.nupkg`. `0.x` versions are marked as pre-release. A second job then pushes the
package to nuget.org through [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing):
no API key is stored; the nuget.org account `vdasus` has a policy that trusts `release.yml` of `vdasus/taskus`,
and the job exchanges its GitHub OIDC token for a short-lived key.

Task file schema changes (a new or renamed key, a changed meaning of a field) go only into minor releases, because
Taskus Studio follows Taskus by major and minor version. A patch release may tighten validation but does not change
the schema.

## Disclaimer

This software is provided "as is", without warranty of any kind. Use it at your own risk. The author accepts no
liability for any damage, data loss or other consequences of its use.

You may use, copy, modify and distribute it for any purpose, commercial or not, under the terms of the
[MIT License](LICENSE).
