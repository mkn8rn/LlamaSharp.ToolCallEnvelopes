using Json.Schema;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class SchemaOptionCombinationTests
{
    private static readonly PrimitiveSpec[] PrimitiveTypes =
    [
        new("string", "string", "\"ok\"", "1", "\"other\""),
        new("integer", "integer", "2", "2.5", "3"),
        new("number", "number", "1.5", "\"bad\"", "2"),
        new("boolean", "boolean", "true", "1", "false"),
        new("null", "null", "null", "false", "false"),
    ];

    public static IEnumerable<TestCaseData> EveryPrimitiveCombination
    {
        get
        {
            foreach (var primitive in PrimitiveTypes)
                foreach (var literal in Enum.GetValues<LiteralConstraint>())
                    foreach (var required in new[] { false, true })
                        foreach (var reference in new[] { false, true })
                        {
                            var combination = new PrimitiveCombination(
                                primitive,
                                literal,
                                required,
                                reference);
                            yield return new TestCaseData(combination).SetName(combination.Name);
                        }
        }
    }

    public static IEnumerable<TestCaseData> EveryArrayCombination
    {
        get
        {
            foreach (var primitive in PrimitiveTypes)
                foreach (var literal in Enum.GetValues<LiteralConstraint>())
                    foreach (var required in new[] { false, true })
                        foreach (var reference in new[] { false, true })
                            foreach (var bounds in Enum.GetValues<ArrayBounds>())
                            {
                                var combination = new ArrayCombination(
                                    primitive,
                                    literal,
                                    required,
                                    reference,
                                    bounds);
                                yield return new TestCaseData(combination).SetName(combination.Name);
                            }
        }
    }

    [TestCaseSource(nameof(EveryPrimitiveCombination))]
    public void EveryPrimitiveConstraintRequirementAndReferenceCombination_AgreesWithReference(
        PrimitiveCombination combination)
    {
        var schema = BuildPrimitiveSchema(combination);
        var turn = Turn(schema);
        var invalid = combination.Literal == LiteralConstraint.None
            ? combination.Primitive.WrongType
            : combination.Primitive.DifferentValue;
        var reference = JsonSchema.FromText(schema);

        AssertAgreement(reference, turn, $"{{\"value\":{combination.Primitive.Valid}}}");
        AssertAgreement(reference, turn, $"{{\"value\":{invalid}}}");
        AssertAgreement(reference, turn, "{}");

        if (combination.Reference)
            turn.Prompt[0].Content.Should().Contain("Reference-site description.");
    }

    [TestCaseSource(nameof(EveryArrayCombination))]
    public void EveryArrayItemConstraintRequirementReferenceAndBoundCombination_AgreesWithReference(
        ArrayCombination combination)
    {
        var schema = BuildArraySchema(combination);
        var turn = Turn(schema);
        var invalidItem = combination.Literal == LiteralConstraint.None
            ? combination.Primitive.WrongType
            : combination.Primitive.DifferentValue;
        var valid = combination.Bounds == ArrayBounds.EmptyOnly
            ? "[]"
            : $"[{combination.Primitive.Valid}]";
        var reference = JsonSchema.FromText(schema);

        AssertAgreement(reference, turn, $"{{\"value\":{valid}}}");
        AssertAgreement(reference, turn, "{}");

        if (combination.Bounds == ArrayBounds.EmptyOnly)
        {
            AssertAgreement(
                reference,
                turn,
                $"{{\"value\":[{combination.Primitive.Valid}]}}");
        }
        else
        {
            AssertAgreement(reference, turn, $"{{\"value\":[{invalidItem}]}}");
        }

        if (combination.Bounds == ArrayBounds.OneOrTwo)
        {
            AssertAgreement(reference, turn, "{\"value\":[]}");
            AssertAgreement(
                reference,
                turn,
                $"{{\"value\":[{combination.Primitive.Valid},{combination.Primitive.Valid},"
                + $"{combination.Primitive.Valid}]}}");
        }

        turn.Prompt[0].Content.Should().Contain("item $[\"value\"][]");
    }

    [Test]
    public void GeneratedSchemaMatrices_CoverEveryDeclaredCombinationExactlyOnce()
    {
        var primitives = EveryPrimitiveCombination
            .SelectMany(test => test.Arguments)
            .Cast<PrimitiveCombination>()
            .ToArray();
        var arrays = EveryArrayCombination
            .SelectMany(test => test.Arguments)
            .Cast<ArrayCombination>()
            .ToArray();

        primitives.Should().HaveCount(60);
        primitives.Select(combination => combination.Name).Should().OnlyHaveUniqueItems();
        arrays.Should().HaveCount(180);
        arrays.Select(combination => combination.Name).Should().OnlyHaveUniqueItems();
    }

    private static ToolEnvelopeTurn Turn(string schema) =>
        TestCatalog.Turn(
            ToolChoice.Required,
            tools: [ToolDefinition.Parse("matrix", "Exercises a schema contract.", schema)]);

    private static void AssertAgreement(
        JsonSchema reference,
        ToolEnvelopeTurn turn,
        string arguments) =>
        SchemaAgreement.AssertValidatesLike(reference, turn, "matrix", arguments);

    private static string BuildPrimitiveSchema(PrimitiveCombination combination)
    {
        var valueSchema = ValueSchema(combination.Primitive, combination.Literal);
        var property = combination.Reference
            ? "{\"$ref\":\"#/$defs/value\",\"description\":\"Reference-site description.\"}"
            : valueSchema;
        var definitions = combination.Reference
            ? $"\"$defs\":{{\"value\":{valueSchema}}},"
            : string.Empty;
        var required = combination.Required ? ",\"required\":[\"value\"]" : string.Empty;
        return $"{{\"type\":\"object\",{definitions}\"properties\":{{\"value\":{property}}}"
               + $"{required},\"additionalProperties\":false}}";
    }

    private static string BuildArraySchema(ArrayCombination combination)
    {
        var itemSchema = ValueSchema(combination.Primitive, combination.Literal);
        var item = combination.Reference ? "{\"$ref\":\"#/$defs/item\"}" : itemSchema;
        var definitions = combination.Reference
            ? $"\"$defs\":{{\"item\":{itemSchema}}},"
            : string.Empty;
        var bounds = combination.Bounds switch
        {
            ArrayBounds.Default => string.Empty,
            ArrayBounds.OneOrTwo => ",\"minItems\":1,\"maxItems\":2",
            ArrayBounds.EmptyOnly => ",\"maxItems\":0",
            _ => throw new ArgumentOutOfRangeException(nameof(combination)),
        };
        var property = $"{{\"type\":\"array\",\"items\":{item}{bounds}}}";
        var required = combination.Required ? ",\"required\":[\"value\"]" : string.Empty;
        return $"{{\"type\":\"object\",{definitions}\"properties\":{{\"value\":{property}}}"
               + $"{required},\"additionalProperties\":false}}";
    }

    private static string ValueSchema(PrimitiveSpec primitive, LiteralConstraint literal)
    {
        var constraint = literal switch
        {
            LiteralConstraint.None => string.Empty,
            LiteralConstraint.Enum => $",\"enum\":[{primitive.Valid}]",
            LiteralConstraint.Const => $",\"const\":{primitive.Valid}",
            _ => throw new ArgumentOutOfRangeException(nameof(literal)),
        };
        return $"{{\"type\":\"{primitive.Type}\"{constraint}}}";
    }

    public enum LiteralConstraint
    {
        None,
        Enum,
        Const,
    }

    public enum ArrayBounds
    {
        Default,
        OneOrTwo,
        EmptyOnly,
    }

    public sealed record PrimitiveSpec(
        string Name,
        string Type,
        string Valid,
        string WrongType,
        string DifferentValue);

    public sealed record PrimitiveCombination(
        PrimitiveSpec Primitive,
        LiteralConstraint Literal,
        bool Required,
        bool Reference)
    {
        public string Name =>
            $"Primitive_{Primitive.Name}_{Literal}_{(Required ? "required" : "optional")}_"
            + (Reference ? "reference" : "inline");
    }

    public sealed record ArrayCombination(
        PrimitiveSpec Primitive,
        LiteralConstraint Literal,
        bool Required,
        bool Reference,
        ArrayBounds Bounds)
    {
        public string Name =>
            $"Array_{Primitive.Name}_{Literal}_{Bounds}_"
            + $"{(Required ? "required" : "optional")}_{(Reference ? "reference" : "inline")}";
    }
}
