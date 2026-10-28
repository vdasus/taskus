using System.Data.Common;
using System.Data.Odbc;

namespace Taskus.Transports;

/// <summary>Any database with an ODBC driver installed on the machine; the connection string names the driver.</summary>
public sealed class OdbcDialect : SqlDialect
{
    public static readonly OdbcDialect Instance = new();

    public override DbConnection Connection(string connection) => new OdbcConnection(connection);

    public override string Describe(DbConnection db)
    {
        var odbc = (OdbcConnection)db;
        return $"{odbc.Driver} {odbc.DataSource}/{odbc.Database}";
    }

    public override string Parameter(int number) => "?";
}
