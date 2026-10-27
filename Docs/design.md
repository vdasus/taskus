# Taskus design

## Purpose

Taskus runs declarative data-transfer tasks of the form "take data from here, convert it, put it there".
Typical tasks:

- Read Excel files from a folder, insert each row into a database.
- Run a `select` against a database, write the result to an Excel file.
- Read an Excel file, send an e-mail for each row.
- Run a `select` against a database, call an external HTTP API for each row.
- Read Excel files with ids, call an HTTP API for each id.

A task is a YAML file. Taskus is a console executable with an `appsettings.json` for logging; the machine that runs it needs neither the
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
taskus schema > task.schema.json            # JSON Schema of task files
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

The file is read in three steps. YAML is turned into a JSON tree and `${secret:...}` references are replaced.
The tree is bound to typed records (`TaskOptions`): an unknown key is an error, so a typo such as `procesed:` stops
the task instead of being ignored. `type` values and HTTP `method` names may use any letter case. The bound task
is then checked by FluentValidation rules (required values, ranges, templates that parse, e-mail addresses,
`smtp` present where needed); every failed rule is reported at once, as `path: message`.

The same records produce the JSON Schema of task files, committed as `schema/task.schema.json` and printed by
`taskus schema`. Editors with a YAML language server (VS Code with the Red Hat YAML extension, JetBrains IDEs)
complete and check task files that start with:

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/vdasus/taskus/main/schema/task.schema.json
```

The schema is stricter than the loader in one way: it wants numbers and booleans unquoted, while the loader also
accepts them as quoted text.

### Folder sources

A source with `folder` and `pattern` (default `*.xlsx`) processes each matching file separately; each file is
one *unit*. The first sheet row holds the field names; `sheet` picks a sheet other than the first. Empty rows are
skipped, and row numbers in the report are sheet row numbers. Excel lock files (`~$*.xlsx`) are ignored.

When nothing matches, the run ends with exit code `0` and writes nothing.

After a file is processed it moves:

- to `processed` when the sink completed it, including a `continue` run that skipped some rows (the report lists
  them; moving such a file to `failed` and running it again would duplicate the rows already written);
- to `failed` when the unit stopped or was rolled back;
- nowhere when the sink could not open (database or mail server unreachable): nothing was read, so the next run
  retries the file.

`processed` and `failed` are optional; a file without a target folder stays in place. A name clash in the target
folder adds a timestamp to the file name.

A `postgres` source is one unit: the result of `query`. An `http` source is one unit: one API response.

### Units

A unit is the scope of one sink session: one Excel output file, one database transaction, one SMTP connection.
The run report has one section per unit.

### Mapping and templates

Mapping expressions and e-mail templates use one engine: Scriban. Templates live inside the task file.
Without `map`, source rows pass to the sink unchanged.

A value that is a single `{{ expression }}` keeps the type of the expression (number, date, null), so a database
sink receives typed values. Any other value renders to text. Field names are case-sensitive inside templates:
`row.Code` reads the column `Code`; `row['Client name']` reads a name with spaces.

Scriban rules to keep in mind: arithmetic on text fails the row (`"x" - 1`), except `*`, which repeats text
(`"x" * 2` is `"xx"`).

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
- `maxErrors` (default `100`, `0` means no limit) stops a `continue` run after the given number of errors,
  counted over all units of the run.

The default is `onError: stop`.

### Transactions (database sinks)

- `transaction: perRow` (default) commits each row separately. Failed rows go to the report; the other rows stay.
- `transaction: all` uses one transaction per unit (one input file). The first error, in mapping or in the
  database, rolls the unit back; the run goes on with the next unit unless `onError: stop`.

Inserts use one prepared statement per unit. Columns are the mapped field names. Taskus reads the column types
from the table and converts values to them: text to numbers, dates and booleans (invariant culture), Excel date
numbers to dates, empty text to `null` for non-text columns. Dates without a time zone are taken as local time.

Batches with a `batchSize` are not supported; add them when a task needs them.

### Excel sink

The file name is a Scriban template; `unit` holds the input file name without extension (`query` for a
database source). `overwrite: true|false` (default `false`) controls an existing file; with `false` the unit fails
before reading, and the input stays in place. `sheet` names the sheet (default `Sheet1`).

With `onError: continue` the file holds the rows that mapped without errors. With `onError: stop` an error leaves
no file. Rows are kept in memory until the unit ends.

```yaml
sink:
  type: excel
  folder: C:\out
  file: "clients_{{ date.now | date.to_string '%Y%m%d_%H%M' }}.xlsx"
  overwrite: false
```

### SMTP sink

Sends one e-mail per row. Mapped fields: `to` (several addresses separated by `,` or `;`), `subject`, `body`,
optional `cc`. `html: true` sends the body as HTML. Server settings come from the task's `smtp` section.
A sent e-mail cannot be recalled, so this sink never rolls back.

### HTTP sink

Calls an HTTP API once per row.

```yaml
map:
  id: "{{ row.Id }}"
sink:
  type: http
  method: POST                  # GET | POST | PUT | PATCH | DELETE, default POST
  url: http://example.com/api   # template over the mapped row
  headers:
    Authorization: "Bearer ${secret:api_token}"
  timeout: 30                   # seconds per attempt, default 100
  retry:
    attempts: 3                 # extra attempts after the first call, default 3
    delay: 2                    # seconds before the first retry, default 1
  rateLimit: 5                  # requests per second, default: no limit
```

- `url` is a template; `row` in it is the mapped row, e.g. `http://example.com/api/{{ row.id | html.url_encode }}`.
  The result must be an absolute `http` or `https` URL.
- POST, PUT and PATCH send the mapped row as a JSON object (`{"id":101}`); GET and DELETE send no body.
- `headers` go on every request; `Content-Type` replaces the default `application/json`.
- Any 2xx status is success. Other statuses, network errors and time-outs fail the row; the error message holds
  the status and the response body (an HTML error page is left out, long bodies are cut).
- The response body is not kept. A call that reached the API cannot be undone, so this sink never rolls back.

### HTTP retries and rate limit

The `http` source and sink share these settings.

- Without a `retry` section there are no retries. With it, network errors, time-outs and statuses 408, 429 and
  5xx are retried `attempts` times. Other 4xx statuses mean the request itself is wrong and are not retried.
- The delay doubles with every attempt (exponential backoff with jitter), starting at `delay` seconds.
  A `Retry-After` header from the server takes precedence, capped at 60 seconds so a single response cannot
  stall a scheduled run.
- Each retry is logged as a warning. After the last attempt the row (sink) or the unit (source) fails with the
  last error.
- `rateLimit` sets the minimum interval between requests: `5` means at most five requests per second, `0.5` one
  request every two seconds. Retries count against the limit. Rows are processed one at a time, so the limit is a
  plain interval, not a queue.
- `timeout` applies to each attempt.

Retries use Polly (`Polly.Core`). The rate limit is a few lines of Taskus code: a sequential pipeline does not
need a rate-limiting library.

### HTTP source

Reads rows from one JSON response.

```yaml
source:
  type: http
  method: GET                   # default GET; POST, PUT, PATCH may send a body
  url: http://example.com/api/clients
  body: '{"from": "{{ date.now | date.to_string "%Y-%m-%d" }}"}'   # optional template, sent as JSON
  headers:
    Authorization: "Bearer ${secret:api_token}"
  items: data.items             # dotted path to the array of rows; default: the response root
  retry:
    attempts: 3
```

- An array element that is an object is one row. Any other element (`[101, 102]`) becomes a row with one field,
  `value`. A single object is one row.
- Nested objects and arrays stay nested, so a mapping can read `row.address.city`.
- Numbers become whole numbers (`long`) when they have no fraction, otherwise `decimal`.
- A failed call, a response that is not JSON, or a missing `items` path fails the unit; the report shows why.
- `url` and `body` are templates without `row` (for example, `date.now`).
- The whole response is read into memory. Pagination is not supported yet.

### Logging and reporting

- Logging is configured in the `Serilog` section of `appsettings.json` next to the executable (format of
  Serilog.Settings.Configuration). The shipped file writes:
  - to the console, `Information` and above;
  - a text log per run, `taskus_<yyyyMMdd_HHmmss>.log`;
  - a JSON log per run, `taskus_<yyyyMMdd_HHmmss>.json`: one JSON object per line (Serilog `JsonFormatter`),
    with the message template and its properties as separate fields, for log tools and scripts.
- In a sink `path`, `{run}` becomes the run start time. A relative path resolves against the log folder: `logs`
  next to the executable, or the folder given by `--logs`. An absolute path is used as is.
- Sinks, levels and output templates change in `appsettings.json` without a rebuild. The executable includes the
  Console and File sinks; another sink package needs a rebuild with that package referenced.
- Without `appsettings.json`, Taskus logs to the console and to a text file per run.
- Logging is structured: every message is a template with named properties (`{Task}`, `{Unit}`, `{Row}`,
  `{Url}`, ...), and the JSON log keeps them as separate fields. Events of a task also carry a `Task` property.

### Log levels

The minimum level is `Information` in the shipped `appsettings.json`. To look into a problem, raise the detail for
one run with `--log-level` (`trace`, `debug`, `information`, `warning`, `error`); it overrides the settings file.
The console stays at `Information`; the files get the extra detail.

| Level         | What is logged                                                                                                                                                                                                                                        |
|---------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Error`       | A unit or run that failed with an exception; a report that could not be saved or sent                                                                                                                                                                 |
| `Warning`     | Row errors, failed units, a stopped run, HTTP retries                                                                                                                                                                                                 |
| `Information` | Unit start and summary (read, written, errors, time), task result, report saved or mailed, moved files                                                                                                                                                |
| `Debug`       | Task settings (no values from secrets), secrets count, files found, database host and database, prepared SQL and column types, commit and rollback, SMTP server, HTTP method, URL template, retry and rate limit, header names, output files, timings |
| `Trace`       | Every source row and mapped row (values), every HTTP call with status and time, every e-mail sent (recipient count only)                                                                                                                              |

Row values appear only at `Trace` and in the first ten rows of a dry run. Sinks never log row data themselves.

### Sensitive data in logs and reports

- Connection strings, passwords, HTTP header values and secret values are never logged on purpose.
- Values from `secrets.yaml` are also masked as `***` wherever they could still show up: in log properties and in
  the error texts of the run report (`.xlsx` and e-mail). This covers, for example, a token in a URL
  (`?key=${secret:api_token}`), which appears in HTTP error messages. Masked are: every top-level text secret,
  values of keys named like `password`, `pwd`, `token`, `secret`, `key` inside secret mappings, and `Password=`
  parts of connection strings. Values shorter than four characters are not masked.
- `logExclude` lists row fields whose values are replaced with `***` wherever rows are logged (trace and dry run):

  ```yaml
  logExclude: [email, personal_code]   # source and mapped field names, case-insensitive
  ```

Known limits (third iteration):

- Error texts can still quote a row value: a failed type conversion (`'abc' cannot be converted to integer`) or
  an HTTP URL built from row fields. `logExclude` does not apply to error texts or to the run report.
- Exception stack traces (logged at `Error`) are not masked.
- A secret written directly into a task file, not through `${secret:...}`, is not known to the mask.
- At the end of a run Taskus builds a summary: rows read, rows written, errors, and the first N errors with the
  source file, row number and reason. With several input files the summary has one section per file.
- `report.folder` saves the summary as an `.xlsx` file (sheets `Summary` and `Errors`). `report.mailTo` sends
  it by e-mail with the file attached. Both follow `report.when` (default `onError`). `mailTo` needs an `smtp`
  section.
- A dry run writes no report.

## Secrets

Connection strings and SMTP credentials live in `secrets.yaml` next to the executable. A task refers to them as
`${secret:<name>}`. `--secrets` points to another file. A value that is only a reference takes the secret as a
whole, so a secret can be a mapping (`smtp: ${secret:smtp_main}`); a reference inside longer text must point to a
text secret.

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

| Kind    | Types                               |
|---------|-------------------------------------|
| Sources | `excel`, `postgres`, `http`         |
| Sinks   | `postgres`, `excel`, `smtp`, `http` |

Also: the run report, `run`, `validate`, `--dry-run`, HTTP retries and rate limit.

First iteration: Excel, PostgreSQL and SMTP transports, the `http` sink. Second iteration: the `http` source,
retries and rate limit.

Example tasks and a secrets template are in `examples/`.

Third iteration (planned):

- `logExclude` for error texts and the run report: drop or mask the values of listed fields in row errors, and
  build URLs in error messages without them; consider masking exception texts.
- Decide whether `logExclude` should also accept nested paths (`address.city`) for `http` source rows.

Not done yet, added when a task needs them: pagination for the `http` source, keeping the API response in the
`http` sink, batched database inserts, other databases.

## Solution layout

```
Taskus.slnx
global.json
Directory.Packages.props        # central package versions
src/Taskus/                     # console executable
tests/Taskus.Tests/
examples/                       # sample tasks, secrets.yaml.example
schema/task.schema.json         # JSON Schema of task files, generated
Docs/
```

There is no separate core library until a second consumer needs one. There is no DI container either:
`CompositionRoot` is the one place that turns validated task options into a runner with its source, mapper and
sink. A test checks that `schema/task.schema.json` matches the model; after changing `TaskOptions`, regenerate it
with `taskus schema > schema/task.schema.json`.

Publishing:

```
dotnet publish src/Taskus -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Every dependency is managed code, so the result is `taskus.exe` plus `appsettings.json`, which stays outside the
executable so it can be edited. Trimming is off.

## Dependencies

All dependencies are free and open source. Versions are pinned in `Directory.Packages.props`.

| Package                                 | Version | License      | Use                                  |
|-----------------------------------------|---------|--------------|--------------------------------------|
| Optionus                                | 0.9.0   | MIT          | Command-line parsing                 |
| Polly.Core                              | 8.8.1   | BSD-3-Clause | HTTP retries                         |
| YamlDotNet                              | 18.1.0  | MIT          | Task and secrets files               |
| FluentValidation                        | 12.2.0  | Apache-2.0   | Task file rules                      |
| Scriban                                 | 7.5.3   | BSD-2-Clause | Mapping expressions and templates    |
| Npgsql                                  | 10.0.3  | PostgreSQL   | PostgreSQL source and sink           |
| MailKit                                 | 4.18.2  | MIT          | SMTP sink and report mail            |
| MiniExcel                               | 1.46.0  | Apache-2.0   | Excel source and sink, streaming     |
| CSharpFunctionalExtensions              | 3.7.0   | MIT          | `Result`, `Maybe`                    |
| Serilog                                 | 4.4.0   | Apache-2.0   | Logging                              |
| Serilog.Sinks.File                      | 7.0.0   | Apache-2.0   | Log file per run                     |
| Serilog.Sinks.Console                   | 6.1.1   | Apache-2.0   | Console log                          |
| Serilog.Settings.Configuration          | 10.0.1  | Apache-2.0   | Logging set up from appsettings.json |
| Microsoft.Extensions.Configuration.Json | 10.0.14 | MIT          | Reads appsettings.json               |

Tests:

| Package                     | Version | License      |
|-----------------------------|---------|--------------|
| xunit.v3                    | 4.0.2   | Apache-2.0   |
| xunit.runner.visualstudio   | 4.0.1   | Apache-2.0   |
| Microsoft.NET.Test.Sdk      | 18.11.0 | MIT          |
| AutoFixture                 | 4.18.1  | MIT          |
| AutoFixture.Xunit3          | 4.19.0  | MIT          |
| AutoFixture.AutoNSubstitute | 4.18.1  | MIT          |
| NSubstitute                 | 5.3.0   | BSD-3-Clause |
| FluentAssertions            | 7.2.2   | Apache-2.0   |

FluentAssertions is pinned to 7.x: version 8 and later use a commercial license. EPPlus is not used for the
same reason. ClosedXML (MIT) is an alternative to MiniExcel if rich formatting is needed; it loads the whole
workbook into memory.

Pick the latest stable version when a dependency is added or updated, and check its license first.
