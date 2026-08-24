using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace HrDashboard.Agents.Tests;

public class SchemaJsonParserTests
{
    [Fact]
    public void TryParseRowValues_ValidTableNameRows_ReturnsValues()
    {
        var json = """[{"table_name":"EMPLOYEES"},{"table_name":"DEPARTMENTS"}]""";

        var success = SchemaJsonParser.TryParseRowValues(json, "table_name", out var values);

        success.Should().BeTrue();
        values.Should().Equal("EMPLOYEES", "DEPARTMENTS");
    }

    [Fact]
    public void TryParseRowValues_CaseInsensitiveKey_ReturnsValues()
    {
        var json = """[{"TABLE_NAME":"EMPLOYEES"}]""";

        var success = SchemaJsonParser.TryParseRowValues(json, "table_name", out var values);

        success.Should().BeTrue();
        values.Should().Equal("EMPLOYEES");
    }

    [Fact]
    public void TryParseRowValues_ErrorString_ReturnsFalse()
    {
        var success = SchemaJsonParser.TryParseRowValues("[SQL error: timeout]", "table_name", out var values);

        success.Should().BeFalse();
        values.Should().BeEmpty();
    }

    [Fact]
    public void TryParseRowValues_KeyMissingFromRows_ReturnsFalse()
    {
        var json = """[{"other_key":"x"}]""";

        var success = SchemaJsonParser.TryParseRowValues(json, "table_name", out var values);

        success.Should().BeFalse();
    }

    [Fact]
    public void ExtractStringResult_RawString_ReturnsItUnchanged()
    {
        var result = SchemaJsonParser.ExtractStringResult("hello");

        result.Should().Be("hello");
    }

    [Fact]
    public void ExtractStringResult_JsonElementString_ReturnsUnderlyingString()
    {
        var element = System.Text.Json.JsonDocument.Parse("\"hello\"").RootElement;

        var result = SchemaJsonParser.ExtractStringResult(element);

        result.Should().Be("hello");
    }

    [Fact]
    public void ExtractStringResult_JsonElementNonString_ReturnsNull()
    {
        var element = System.Text.Json.JsonDocument.Parse("42").RootElement;

        var result = SchemaJsonParser.ExtractStringResult(element);

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractStringResult_NullOrOtherType_ReturnsNull()
    {
        SchemaJsonParser.ExtractStringResult(null).Should().BeNull();
        SchemaJsonParser.ExtractStringResult(42).Should().BeNull();
    }

    [Fact]
    public void ExtractStringResult_TextContent_ReturnsUnderlyingText()
    {
        var content = new TextContent("hello");

        var result = SchemaJsonParser.ExtractStringResult(content);

        result.Should().Be("hello");
    }
}
