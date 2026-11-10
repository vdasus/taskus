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

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Priority", "High")]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(double))]
    [InlineData(typeof(float))]
    public void Convert_CommaDecimalText_FailsInsteadOfDroppingTheComma(Type type)
    {
        var result = DbValues.Convert("12,50", type, "numeric");

        result.IsFailure.Should().BeTrue("the comma must not be read as a thousands separator, which gives 1250");
        result.Error.Should().Be("'12,50' cannot be converted to numeric");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Convert_NumberThatIsNoDate_Fails()
    {
        var result = DbValues.Convert(99999999d, typeof(DateTime), "date");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("'99999999' cannot be converted to date");
    }
}
