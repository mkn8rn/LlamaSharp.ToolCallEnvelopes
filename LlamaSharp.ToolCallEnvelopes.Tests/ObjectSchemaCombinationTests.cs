using Json.Schema;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class ObjectSchemaCombinationTests
{
    private static readonly string[] PropertyNames = ["alpha", "beta", "gamma"];

    public static IEnumerable<TestCaseData> EveryPropertyOrderAndRequiredSet
    {
        get
        {
            foreach (var declarationOrder in Permutations(PropertyNames))
                foreach (var requiredMask in Enumerable.Range(0, 8))
                {
                    yield return new TestCaseData(string.Join(",", declarationOrder), requiredMask)
                        .SetName(
                            $"Object_order_{string.Join('_', declarationOrder)}_required_{requiredMask}");
                }
        }
    }

    public static IEnumerable<TestCaseData> EveryNestedRequirementAndReferenceShape
    {
        get
        {
            foreach (var outerRequired in new[] { false, true })
                foreach (var innerRequired in new[] { false, true })
                    foreach (var reference in new[] { false, true })
                    {
                        yield return new TestCaseData(outerRequired, innerRequired, reference)
                            .SetName(
                                $"Nested_outer_{outerRequired}_inner_{innerRequired}_ref_{reference}");
                    }
        }
    }

    [TestCaseSource(nameof(EveryPropertyOrderAndRequiredSet))]
    public void EveryPropertyOrderRequiredSetAndInstanceOrder_AgreesWithJsonSchema(
        string declarationOrderText,
        int requiredMask)
    {
        var declarationOrder = declarationOrderText.Split(',');
        var schema = BuildFlatSchema(declarationOrder, requiredMask);
        var reference = JsonSchema.FromText(schema);
        var turn = Turn(schema);

        foreach (var instanceMask in Enumerable.Range(0, 8))
        {
            var selected = PropertyNames
                .Where((_, index) => (instanceMask & (1 << index)) != 0)
                .ToArray();
            foreach (var instanceOrder in Permutations(selected))
            {
                var arguments = BuildInstance(instanceOrder);
                SchemaAgreement.AssertValidatesLike(
                    reference,
                    turn,
                    "object_matrix",
                    arguments);
            }
        }

        SchemaAgreement.AssertValidatesLike(
            reference,
            turn,
            "object_matrix",
            "{\"extra\":true}");
        SchemaAgreement.AssertValidatesLike(
            reference,
            turn,
            "object_matrix",
            "{\"alpha\":1,\"beta\":2,\"gamma\":true}");
        SchemaAgreement.AssertValidatesLike(
            reference,
            turn,
            "object_matrix",
            "{\"alpha\":\"ok\",\"beta\":\"wrong\",\"gamma\":true}");
        SchemaAgreement.AssertValidatesLike(
            reference,
            turn,
            "object_matrix",
            "{\"alpha\":\"ok\",\"beta\":2,\"gamma\":0}");

        var duplicate = BuildDuplicateInstance(requiredMask);
        turn.TryParse(TestCatalog.ToolRequest("object_matrix", duplicate), out _, out var error)
            .Should().BeFalse();
        error!.Code.Should().Be(ToolEnvelopeErrorCode.SchemaViolation);
        error.JsonPointer.Should().Be("/tool_calls/0/arguments/alpha");
    }

    [TestCaseSource(nameof(EveryNestedRequirementAndReferenceShape))]
    public void EveryNestedRequirementAndReferenceShape_AgreesWithJsonSchema(
        bool outerRequired,
        bool innerRequired,
        bool reference)
    {
        var schema = BuildNestedSchema(outerRequired, innerRequired, reference);
        var jsonSchema = JsonSchema.FromText(schema);
        var turn = Turn(schema);
        var instances = new[]
        {
            "{}",
            "{\"container\":{}}",
            "{\"container\":{\"value\":\"ok\"}}",
            "{\"container\":{\"value\":1}}",
            "{\"container\":{\"extra\":true}}",
            "{\"extra\":true}",
        };

        foreach (var arguments in instances)
        {
            SchemaAgreement.AssertValidatesLike(
                jsonSchema,
                turn,
                "object_matrix",
                arguments);
        }

        if (reference)
            turn.Prompt[0].Content.Should().Contain("Reference-site container description.");
        if (innerRequired && !outerRequired)
        {
            turn.Prompt[0].Content.Should().Contain(
                "arg $[\"container\"][\"value\"]: string, required when its parent is present");
        }
    }

    [Test]
    public void SemanticCatalog_UsesUnambiguousPathsForPunctuationInPropertyNames()
    {
        const string schema =
            "{\"type\":\"object\",\"properties\":{\"a.b\":{\"type\":\"object\","
            + "\"properties\":{\"x/y~z[]\":{\"type\":\"string\"}},"
            + "\"additionalProperties\":false}},\"additionalProperties\":false}";

        var prompt = Turn(schema).Prompt[0].Content;

        prompt.Should().Contain("arg $[\"a.b\"]: object")
            .And.Contain("arg $[\"a.b\"][\"x/y~z[]\"]: string");
    }

    [Test]
    public void GeneratedObjectMatrices_CoverEveryDeclaredCombinationExactlyOnce()
    {
        var flat = EveryPropertyOrderAndRequiredSet.ToArray();
        var nested = EveryNestedRequirementAndReferenceShape.ToArray();

        flat.Should().HaveCount(48);
        nested.Should().HaveCount(8);
        flat.Select(test => test.TestName).Should().OnlyHaveUniqueItems();
        nested.Select(test => test.TestName).Should().OnlyHaveUniqueItems();
    }

    private static ToolEnvelopeTurn Turn(string schema) =>
        TestCatalog.Turn(
            ToolChoice.Required,
            tools:
            [
                ToolDefinition.Parse(
                    "object_matrix",
                    "Exercises closed object combinations.",
                    schema),
            ]);

    private static string BuildFlatSchema(IReadOnlyList<string> declarationOrder, int requiredMask)
    {
        var properties = declarationOrder.Select(name =>
            $"{System.Text.Json.JsonSerializer.Serialize(name)}:{PropertySchema(name)}");
        var required = PropertyNames
            .Where((_, index) => (requiredMask & (1 << index)) != 0)
            .Select(name => System.Text.Json.JsonSerializer.Serialize(name))
            .ToArray();
        var requiredJson = required.Length == 0
            ? string.Empty
            : $",\"required\":[{string.Join(',', required)}]";
        return "{\"type\":\"object\",\"properties\":{"
               + string.Join(',', properties)
               + "}"
               + requiredJson
               + ",\"additionalProperties\":false}";
    }

    private static string BuildNestedSchema(
        bool outerRequired,
        bool innerRequired,
        bool reference)
    {
        var nested = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}}"
                     + (innerRequired ? ",\"required\":[\"value\"]" : string.Empty)
                     + ",\"additionalProperties\":false}";
        var definitions = reference ? $"\"$defs\":{{\"container\":{nested}}}," : string.Empty;
        var container = reference
            ? "{\"$ref\":\"#/$defs/container\","
              + "\"description\":\"Reference-site container description.\"}"
            : nested;
        return "{\"type\":\"object\","
               + definitions
               + $"\"properties\":{{\"container\":{container}}}"
               + (outerRequired ? ",\"required\":[\"container\"]" : string.Empty)
               + ",\"additionalProperties\":false}";
    }

    private static string BuildInstance(IEnumerable<string> order) =>
        "{" + string.Join(',', order.Select(name =>
            $"{System.Text.Json.JsonSerializer.Serialize(name)}:{PropertyValue(name)}")) + "}";

    private static string BuildDuplicateInstance(int requiredMask)
    {
        var values = new List<string> { "\"alpha\":\"first\"", "\"alpha\":\"second\"" };
        if ((requiredMask & 2) != 0)
            values.Add("\"beta\":2");
        if ((requiredMask & 4) != 0)
            values.Add("\"gamma\":true");
        return "{" + string.Join(',', values) + "}";
    }

    private static string PropertySchema(string name) => name switch
    {
        "alpha" => "{\"type\":\"string\"}",
        "beta" => "{\"type\":\"integer\"}",
        "gamma" => "{\"type\":\"boolean\"}",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown matrix property."),
    };

    private static string PropertyValue(string name) => name switch
    {
        "alpha" => "\"ok\"",
        "beta" => "2",
        "gamma" => "true",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown matrix property."),
    };

    private static IEnumerable<T[]> Permutations<T>(IReadOnlyList<T> values)
    {
        if (values.Count == 0)
        {
            yield return [];
            yield break;
        }

        for (var index = 0; index < values.Count; index++)
        {
            var head = values[index];
            var tail = values.Where((_, candidate) => candidate != index).ToArray();
            foreach (var permutation in Permutations(tail))
                yield return [head, .. permutation];
        }
    }
}
