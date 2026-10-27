# Taskus design

## Purpose

Taskus runs declarative data-transfer tasks of the form "take data from here, convert it, put it there".
Typical tasks:

- Read Excel files from a folder, insert each row into a database.
- Run a `select` against a database, write the result to an Excel file.
- Read an Excel file, send an e-mail for each row.
- Run a `select` against a database, call an external HTTP API for each row.

A task is a YAML file. Taskus is a single console executable; the machine that runs it needs neither the
.NET SDK nor the .NET runtime.

## Model

Every task is one pipeline: **source → map → sink**.

```csharp
public interface ISource { IAsyncEnumerable<Row> ReadAsync(CancellationToken ct); }
public interface ISink   { Task<UnitResult<Error>> WriteAsync(Row row, CancellationToken ct); }
```

- `Row` is a dictionary of field name to value (`IReadOnlyDictionary<string, object?>`). Tasks are declarative,
  so typed records add nothing.
- The map step turns a source row into a sink row. Each target field is a Scriban expression over `row`.
- Sources and sinks are registered by the `type:` value from the task file. A new transport is one class and one
  registration line.
- Reading is streaming (`IAsyncEnumerable`), so tens of thousands of rows do not need to fit in memory.
- Per-row results use `Result` / `UnitResult` from CSharpFunctionalExtensions. A failed row does not stop the run
  unless the error policy says so.

Longer flows (database → Excel, then Excel → HTTP API) are separate tasks run one after another.

## Command line

Parsed with Optionus.

```
taskus run tasks/import-clients.yaml        # one task
taskus run tasks/                           # every task in a folder
taskus run tasks/import-clients.yaml --dry-run
taskus validate tasks/                      # check task files without running them
```

`--dry-run` reads and maps, logs what would be written, and writes nothing.

Exit codes: `0` success, `1` the run had row errors, `2` a task file is invalid or a run could not start.
Windows Task Scheduler can act on these codes.

## Task file

```yaml
name: import-clients
source:
  type: excel
  folder: C:\in\clients
  pattern: "*.xlsx"
  sheet: Sheet1            # default: the first sheet
  processed: C:\in\clients\done
  failed: C:\in\clients\failed
map:
  code:  "{{ row.Code | string.strip }}"
  name:  "{{ row['Client name'] }}"
  limit: "{{ row.Limit ?? 0 }}"
sink:
  type: postgres
  connection: ${secret:pg_main}
  table: public.clients
  transaction: perRow      # perRow | all
onError: continue          # stop | continue
maxErrors: 100             # stop anyway after this many errors
report:
  folder: C:\out\reports   # run report as .xlsx
  mailTo: me@example.com
  when: onError            # always | onError | never
smtp: ${secret:smtp_main}
```

### Folder sources

A source with `folder` and `pattern` processes each matching file separately. When nothing matches, the run
ends with exit code `0` and writes nothing. After processing, each file moves to `processed` or `failed`, so the
next scheduled run does not pick it up again.

### Mapping and templates

Mapping expressions and e-mail templates use one engine: Scriban. Templates live inside the task file.

```yaml
map:
  to:      "{{ row.email }}"
  subject: "Invoice {{ row.id }}"
  body: |
    Hello {{ row.name }},
    amount due: {{ row.amount | math.format "0.00" }}
```

### Errors

- `onError: stop` ends the run at the first failed row.
- `onError: continue` skips the failed row and records it in the report.
- `maxErrors` stops a `continue` run after the given number of errors.

### Transactions (database sinks)

- `transaction: perRow` commits each row separately. Failed rows go to the report; the other rows stay.
- `transaction: all` uses one transaction per run and inserts through `COPY`. Any error, in mapping or in the
  database, rolls back the whole run.

Batches with a `batchSize` are not supported: with `COPY`, one bad row fails the whole batch anyway.

### Excel sink

The file name is a Scriban template. `overwrite: true|false` controls an existing file.

```yaml
sink:
  type: excel
  folder: C:\out
  file: "clients_{{ date.now | date.to_string '%Y%m%d_%H%M' }}.xlsx"
  overwrite: false
```

### Logging and reporting

- Each run writes a log file.
- At the end of a run Taskus builds a summary: rows read, rows written, errors, and the first N errors with the
  source file, row number and reason. With several input files the summary has one section per file.
- `report.folder` saves the summary as an `.xlsx` file. `report.mailTo` sends it by e-mail, according to
  `report.when`.

## Secrets

Connection strings and SMTP credentials live in `secrets.yaml` next to the executable. A task refers to them as
`${secret:<name>}`.

```yaml
pg_main: Host=db;Database=main;Username=taskus;Password=...
smtp_main:
  server: smtp.example.com
  port: 587
  user: taskus
  password: ...
  from: noreply@example.com
```

`secrets.yaml` holds passwords in plain text. Restrict its NTFS permissions to the accounts that run Taskus
(the user account and the Task Scheduler account). The file is in `.gitignore` and is never committed.
A stronger store (DPAPI, a vault) can replace it later.

## Scope

First iteration:

| Kind    | Types                       |
|---------|-----------------------------|
| Sources | `excel`, `postgres`         |
| Sinks   | `postgres`, `excel`, `smtp` |

Also in the first iteration: the run report, `run`, `validate`, `--dry-run`.

Second iteration: `http` source and sink. Other databases are added when a task needs them.

## Solution layout

```
Taskus.slnx
global.json
Directory.Packages.props        # central package versions
src/Taskus/                     # console executable
tests/Taskus.Tests/
Docs/
```

There is no separate core library until a second consumer needs one.

Publishing:

```
dotnet publish src/Taskus -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Every dependency is managed code, so the result is a single `taskus.exe`. Trimming is off.

## Dependencies

All dependencies are free and open source. Versions are pinned in `Directory.Packages.props`.

| Package                     | Version | License      | Use                               |
|-----------------------------|---------|--------------|-----------------------------------|
| Optionus                    | 0.9.0   | MIT          | Command-line parsing              |
| YamlDotNet                  | 18.1.0  | MIT          | Task and secrets files            |
| Scriban                     | 7.5.3   | BSD-2-Clause | Mapping expressions and templates |
| Npgsql                      | 10.0.3  | PostgreSQL   | PostgreSQL source and sink        |
| MailKit                     | 4.18.2  | MIT          | SMTP sink and report mail         |
| MiniExcel                   | 1.46.0  | Apache-2.0   | Excel source and sink, streaming  |
| CSharpFunctionalExtensions  | 3.7.0   | MIT          | `Result`, `Maybe`                 |
| Serilog                     | 4.4.0   | Apache-2.0   | Logging                           |
| Serilog.Sinks.File          | 7.0.0   | Apache-2.0   | Log file per run                  |
| Serilog.Sinks.Console       | 6.1.1   | Apache-2.0   | Console log                       |

Tests:

| Package                     | Version | License      |
|-----------------------------|---------|--------------|
| xunit.v3                    | 4.0.2   | Apache-2.0   |
| xunit.runner.visualstudio   | 4.0.1   | Apache-2.0   |
| Microsoft.NET.Test.Sdk      | 18.11.0 | MIT          |
| AutoFixture                 | 4.18.1  | MIT          |
| AutoFixture.Xunit3          | 4.19.0  | MIT          |
| AutoFixture.AutoNSubstitute | 4.18.1  | MIT          |
| NSubstitute                 | 6.2.0   | BSD-3-Clause |
| FluentAssertions            | 7.2.2   | Apache-2.0   |

FluentAssertions is pinned to 7.x: version 8 and later use a commercial license. EPPlus is not used for the
same reason. ClosedXML (MIT) is an alternative to MiniExcel if rich formatting is needed; it loads the whole
workbook into memory.

Pick the latest stable version when a dependency is added or updated, and check its license first.
