# Taskus.Core

The library behind [Taskus](https://github.com/vdasus/taskus): the task file model (`TaskOptions`), loading and
validation (`TaskLoader`, `TaskValidation`), secrets (`Secrets`), the Scriban row mapping (`RowMapper`), and the
sources and sinks (Excel, PostgreSQL, ODBC, HTTP, SMTP), listed in `Connectors`.

It is published for [Taskus Studio](https://github.com/vdasus/taskus-studio), the task file editor. The API follows
the needs of the `taskus` CLI and Taskus Studio and may change between minor versions.
