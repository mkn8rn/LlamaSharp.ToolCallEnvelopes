using System.Text.Json;
using FluentAssertions;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class LlamaSharpJsonSchemaConverterTests
{
    [SetUp]
    public void ResetCache() => LlamaSharpJsonSchemaConverter.ResetCache();

    [Test]
    public void TryConvert_ObjectWithRequiredAndClosedProperties_EmitsNamedRules()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "age": { "type": "integer" }
              },
              "required": ["name"],
              "additionalProperties": false
            }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            document.RootElement,
            out var grammar,
            out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().BeEmpty();
        grammar.Should().Contain("\\\"name\\\"");
        grammar.Should().Contain("\\\"age\\\"");
        grammar.Should().Contain("integer");
    }

    [Test]
    public void TryConvert_PatternString_UsesRegexFragmentWhenSupported()
    {
        using var document = JsonDocument.Parse("""{ "type": "string", "pattern": "^foo$" }""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            document.RootElement,
            out var grammar,
            out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().BeEmpty();
        grammar.Should().Contain("\"f\" \"o\" \"o\"");
    }

    [Test]
    public void TryConvert_UnsupportedPattern_TracksUnsupportedAndRelaxesToString()
    {
        using var document = JsonDocument.Parse("""{ "type": "string", "pattern": "^(a)\\1$" }""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            document.RootElement,
            out var grammar,
            out var unsupported);

        ok.Should().BeTrue();
        grammar.Should().Contain("string");
        unsupported.Should().Contain(u => u.Contains("/pattern", StringComparison.Ordinal));
    }

    [Test]
    public void TryConvert_FormatsUseDedicatedFragmentsWhenSupported()
    {
        using var document = JsonDocument.Parse("""{ "type": "string", "format": "uuid" }""");

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            document.RootElement,
            out var grammar,
            out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().BeEmpty();
        grammar.Should().Contain("{8}");
        LlamaSharpStringFormatGrammars.TryGet("email").Should().NotBeNull();
        LlamaSharpStringFormatGrammars.TryGet("regex").Should().BeNull();
    }

    [Test]
    public void TryConvert_OneOfIsRelaxedAndReported()
    {
        using var document = JsonDocument.Parse("""
            {
              "oneOf": [
                { "type": "string" },
                { "type": "integer" }
              ]
            }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            document.RootElement,
            out var grammar,
            out var unsupported);

        ok.Should().BeTrue();
        grammar.Should().Contain("string");
        grammar.Should().Contain("integer");
        unsupported.Should().Contain(u => u.Contains("/oneOf", StringComparison.Ordinal));
    }

    [Test]
    public void TryConvert_LocalRefResolvesDefs()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": { "item": { "$ref": "#/$defs/Item" } },
              "required": ["item"],
              "$defs": {
                "Item": {
                  "type": "object",
                  "properties": { "id": { "type": "integer" } },
                  "required": ["id"]
                }
              }
            }
            """);

        var ok = LlamaSharpJsonSchemaConverter.TryConvert(
            document.RootElement,
            out var grammar,
            out var unsupported);

        ok.Should().BeTrue();
        unsupported.Should().BeEmpty();
        grammar.Should().Contain("\\\"id\\\"");
    }

    [Test]
    public void RegexConverter_RejectsUnsupportedLookaheadAndShorthand()
    {
        LlamaSharpRegexToGrammar.TryConvert("(?=abc)", out _).Should().BeFalse();
        LlamaSharpRegexToGrammar.TryConvert(@"\d+", out _).Should().BeFalse();
        LlamaSharpRegexToGrammar.TryConvert("[a-z0-9]+", out var grammar).Should().BeTrue();
        grammar.Should().Contain("a-z0-9");
    }
}
