# Taskus

A console tool for declarative data-transfer tasks: take data from a source (Excel files, a PostgreSQL query),
convert each row, and put it into a sink (a database table, an Excel file, e-mail).

Each task is a YAML file. Taskus runs it manually or from Windows Task Scheduler: it checks the input,
processes what it finds, reports the result, and exits.

Status: design stage. See [Docs/design.md](Docs/design.md).

## Disclaimer

This software is provided "as is", without warranty of any kind. Use it at your own risk. The author accepts no
liability for any damage, data loss or other consequences of its use.

You may use, copy, modify and distribute it for any purpose, commercial or not, under the terms of the
[MIT License](LICENSE).
