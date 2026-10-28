using FluentAssertions;
using Taskus.Transports;

namespace Taskus.Tests.Transports;

public class DbValuesTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("42", typeof(int), "integer", 42)]
    [InlineData(42.0, typeof(int), "integer", 42)]
    [InlineData("12.5", typeof(decimal), "numeric", "12.5")]
    [InlineData(7, typeof(string), "text", "7")]
    [InlineData("yes", typeof(bool), "boolean", true)]
    public void Convert_ToColumnType(object value, Type type, string dataType, object expected)
    {
        var result = DbValues.Convert(value, type, dataType);

        var want = type == typeof(decimal) ? decimal.Parse((string)expected, System.Globalization.CultureInfo.InvariantCulture) : expected;
        result.Value.Should().Be(want);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(null)]
    [InlineData("")]
    public void Convert_EmptyToNonText_GivesNull(object? value)
    {
        DbValues.Convert(value, typeof(int), "integer").Value.Should().Be(DBNull.Value);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Convert_ExcelDateNumber_ToTimestamp()
    {
        var result = DbValues.Convert(new DateTime(2026, 10, 27).ToOADate(), typeof(DateTime), "timestamp without time zone");

        result.Value.Should().Be(new DateTime(2026, 10, 27));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Postgres_LocalDate_ToTimestamptz_IsUtc()
    {
        var local = new DateTime(2026, 10, 27, 12, 0, 0);

        var result = (DateTime)PostgresDialect.Instance.Convert(local, typeof(DateTime), "timestamp with time zone").Value;

        result.Kind.Should().Be(DateTimeKind.Utc);
        result.ToLocalTime().Should().Be(local);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Convert_BadText_Fails()
    {
        var result = DbValues.Convert("abc", typeof(int), "integer");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("'abc' cannot be converted to integer");
    }
}
