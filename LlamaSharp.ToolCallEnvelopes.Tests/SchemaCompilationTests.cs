namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class SchemaCompilationTests
{
    public static IEnumerable<TestCaseData> UnsupportedSchemas
    {
        get
        {
            yield return Invalid(
                """{"properties":{},"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.InvalidSchema,
                "/type",
                "missing type");
            yield return Invalid(
                """{"type":"array","items":{"type":"string"}}""",
                ToolEnvelopePlanDiagnosticCode.NonObjectSchema,
                "/type",
                "non-object root");
            yield return Invalid(
                """{"type":"object","properties":{}}""",
                ToolEnvelopePlanDiagnosticCode.OpenObject,
                "/additionalProperties",
                "missing closed-object declaration");
            yield return Invalid(
                """{"type":"object","properties":{},"additionalProperties":true}""",
                ToolEnvelopePlanDiagnosticCode.OpenObject,
                "/additionalProperties",
                "open object");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"string\",\"pattern\":\"abc\"}"),
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/properties/x/pattern",
                "pattern");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"string\",\"format\":\"date\"}"),
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/properties/x/format",
                "format");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"string\",\"allOf\":[]}"),
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/properties/x/allOf",
                "allOf");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"string\",\"oneOf\":[]}"),
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/properties/x/oneOf",
                "oneOf");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"string\",\"anyOf\":[]}"),
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/properties/x/anyOf",
                "anyOf");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"string\",\"not\":{}}"),
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/properties/x/not",
                "not");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"integer\",\"exclusiveMinimum\":0}"),
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/properties/x/exclusiveMinimum",
                "exclusive bound");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"uniqueItems\":true}"),
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/properties/x/uniqueItems",
                "unique items");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false,\"const\":{}}"),
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/properties/x/const",
                "object const");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"enum\":[[]]}"),
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/properties/x/enum",
                "array enum");
            yield return Invalid(
                """{"$id":"https://example.com/tool","type":"object","properties":{},"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/$id",
                "root identifier");
            yield return Invalid(
                Closed("\"x\":{\"$anchor\":\"value\",\"type\":\"string\"}"),
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/properties/x/$anchor",
                "nested anchor");
            yield return Invalid(
                Closed(
                    "\"x\":{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\","
                    + "\"type\":\"string\"}"),
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/properties/x/$schema",
                "nested dialect");
            yield return Invalid(
                """{"$schema":"http://json-schema.org/draft-07/schema#","type":"object","properties":{},"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/$schema",
                "unsupported root dialect");
            yield return Invalid(
                """{"$schema":2020,"type":"object","properties":{},"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/$schema",
                "non-string root dialect");
            yield return Invalid(
                """{"type":"object","properties":{},"required":["missing"],"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.InvalidRequiredProperty,
                "/required",
                "unknown required property");
            yield return Invalid(
                """{"type":"object","properties":{"x":{"type":"string"}},"required":["x","x"],"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.InvalidRequiredProperty,
                "/required/1",
                "duplicate required property");
            yield return Invalid(
                """{"type":"object","properties":{"x":{"type":"string"}},"required":[1],"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.InvalidRequiredProperty,
                "/required/0",
                "non-string required property");
            yield return Invalid(
                """{"type":"object","properties":[],"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.InvalidSchema,
                "/properties",
                "invalid properties container");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"array\"}"),
                ToolEnvelopePlanDiagnosticCode.InvalidSchema,
                "/properties/x/items",
                "missing item schema");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"array\",\"items\":\"string\"}"),
                ToolEnvelopePlanDiagnosticCode.InvalidSchema,
                "/properties/x/items",
                "non-object item schema");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"string\",\"minLength\":5,\"maxLength\":2}"),
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                "/properties/x",
                "inverted string bounds");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":5,\"maxItems\":2}"),
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                "/properties/x",
                "inverted array bounds");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"number\",\"minimum\":5,\"maximum\":2}"),
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                "/properties/x",
                "inverted numeric bounds");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"string\",\"enum\":[]}"),
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                "/properties/x/enum",
                "empty enum");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"string\",\"enum\":[1]}"),
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                "/properties/x/enum/0",
                "wrong enum type");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"integer\",\"const\":1.5}"),
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                "/properties/x/const",
                "wrong const type");
            yield return Invalid(
                Closed("\"x\":{\"type\":\"string\",\"enum\":[\"a\"],\"const\":\"b\"}"),
                ToolEnvelopePlanDiagnosticCode.InvalidConstraint,
                "/properties/x",
                "const outside enum");
            yield return Invalid(
                Closed("\"x\":{\"$ref\":\"#/$defs/missing\"}"),
                ToolEnvelopePlanDiagnosticCode.UnknownReference,
                "/properties/x/$ref",
                "unknown reference");
            yield return Invalid(
                Closed("\"x\":{\"$ref\":\"https://example.com/schema\"}"),
                ToolEnvelopePlanDiagnosticCode.UnknownReference,
                "/properties/x/$ref",
                "external reference");
            yield return Invalid(
                """
                {
                  "type":"object",
                  "$defs":{"node":{"type":"object","properties":{"next":{"$ref":"#/$defs/node"}},"additionalProperties":false}},
                  "properties":{"node":{"$ref":"#/$defs/node"}},
                  "additionalProperties":false
                }
                """,
                ToolEnvelopePlanDiagnosticCode.CircularReference,
                "/$defs/node/properties/next/$ref",
                "recursive reference");
            yield return Invalid(
                """{"type":"object","type":"object","properties":{},"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.DuplicateSchemaProperty,
                "/type",
                "duplicate schema keyword");
            yield return Invalid(
                """{"type":"object","properties":{"x":{"type":"string"},"x":{"type":"string"}},"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.DuplicateSchemaProperty,
                "/properties/x",
                "duplicate parameter property");
            yield return Invalid(
                """{"type":"object","definitions":{},"properties":{},"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/definitions",
                "non-profile definitions keyword");
            yield return Invalid(
                """{"type":"object","$defs":[],"properties":{},"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.InvalidSchema,
                "/$defs",
                "non-object definitions container");
            yield return Invalid(
                """{"type":"object","$defs":{"x":{"type":"string"},"x":{"type":"number"}},"properties":{},"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.DuplicateSchemaProperty,
                "/$defs/x",
                "duplicate definition name");
            yield return Invalid(
                """{"type":"object","$defs":{"unused":{"type":"string","pattern":"x"}},"properties":{},"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.UnsupportedKeyword,
                "/$defs/unused/pattern",
                "unsupported keyword in unused definition");
            yield return Invalid(
                """{"type":"object","$defs":{"unused":{"$ref":"#/$defs/unused"}},"properties":{},"additionalProperties":false}""",
                ToolEnvelopePlanDiagnosticCode.CircularReference,
                "/$defs/unused/$ref",
                "unused recursive definition");
            yield return Invalid(
                """
                {
                  "type":"object",
                  "$defs":{"a~2b":{"type":"string"}},
                  "properties":{"x":{"$ref":"#/$defs/a~2b"}},
                  "additionalProperties":false
                }
                """,
                ToolEnvelopePlanDiagnosticCode.UnknownReference,
                "/properties/x/$ref",
                "invalid JSON Pointer escape");
        }
    }

    [Test]
    public void Compile_AcceptsTheExactBoundedProfileAndAcyclicReferences()
    {
        const string schema =
            """
            {
              "$schema":"https://json-schema.org/draft/2020-12/schema",
              "title":"Request",
              "type":"object",
              "$defs":{
                "address":{
                  "type":"object",
                  "properties":{
                    "city":{"type":"string","minLength":1,"maxLength":64},
                    "country":{"type":"string","const":"HR"}
                  },
                  "required":["city","country"],
                  "additionalProperties":false
                },
                "unused":{"type":"string","maxLength":4}
              },
              "properties":{
                "address":{"$ref":"#/$defs/address","description":"Destination."},
                "priority":{"type":"integer","minimum":1,"maximum":5},
                "ratio":{"type":"number","minimum":0,"maximum":1},
                "enabled":{"type":"boolean","enum":[true]},
                "nothing":{"type":"null"},
                "labels":{"type":"array","items":{"type":"string","maxLength":20},"maxItems":3}
              },
              "required":["address","priority"],
              "additionalProperties":false
            }
            """;

        var plan = TestCatalog.Plan(tools: [ToolDefinition.Parse("submit", "Submits data.", schema)]);
        var turn = plan.CreateTurn("Submit valid data.", [], ToolChoice.Required);
        var raw = TestCatalog.ToolRequest(
            "submit",
            """{"address":{"city":"Zagreb","country":"HR"},"priority":3,"ratio":0.5,"enabled":true,"nothing":null,"labels":["a","b"]}""");

        turn.Parse(raw).Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>();
        plan.Metrics.MaximumSchemaDepth.Should().BeGreaterThanOrEqualTo(3);
        turn.Grammar.Should().Contain("t0-schema-").And.NotContain("object-kv");
    }

    [TestCaseSource(nameof(UnsupportedSchemas))]
    public void Compile_RejectsUnsupportedOrAmbiguousSchemas(
        string schema,
        ToolEnvelopePlanDiagnosticCode expectedCode,
        string expectedPointer)
    {
        var tool = ToolDefinition.Parse("test", "Tests one schema.", schema);

        var act = () => TestCatalog.Plan(tools: [tool]);

        act.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == expectedCode
                && diagnostic.JsonPointer == expectedPointer);
    }

    [Test]
    public void Compile_EscapesJsonPointerSegmentsInDiagnostics()
    {
        var schema = Closed("\"a/b~c\":{\"type\":\"string\",\"pattern\":\"x\"}");

        var act = () => TestCatalog.Plan(
            tools: [ToolDefinition.Parse("test", "Tests pointers.", schema)]);

        act.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.JsonPointer == "/properties/a~1b~0c/pattern");
    }

    [Test]
    public void Compile_RejectsSchemaDepthPropertyEnumTextAndRuleBudgets()
    {
        const string deep =
            """
            {
              "type":"object",
              "properties":{
                "a":{"type":"object","properties":{
                  "b":{"type":"object","properties":{
                    "c":{"type":"string"}
                  },"additionalProperties":false}
                },"additionalProperties":false}
              },
              "additionalProperties":false
            }
            """;
        var manyProperties = string.Join(
            ",",
            Enumerable.Range(0, 4).Select(index => $"\"p{index}\":{{\"type\":\"string\"}}"));
        var manyEnums = string.Join(",", Enumerable.Range(0, 4).Select(index => $"\"v{index}\""));

        var depthAct = () => TestCatalog.Plan(
            tools: [ToolDefinition.Parse("deep", "Deep.", deep)],
            limits: ToolEnvelopeLimits.Constrained with { MaxSchemaDepth = 3 });
        var propertyAct = () => TestCatalog.Plan(
            tools: [ToolDefinition.Parse("wide", "Wide.", Closed(manyProperties))],
            limits: ToolEnvelopeLimits.Constrained with { MaxPropertiesPerObject = 3 });
        var enumAct = () => TestCatalog.Plan(
            tools: [ToolDefinition.Parse(
                "enum_test",
                "Enum.",
                Closed($"\"value\":{{\"type\":\"string\",\"enum\":[{manyEnums}]}}"))],
            limits: ToolEnvelopeLimits.Constrained with { MaxEnumValues = 3 });
        var enumTextAct = () => TestCatalog.Plan(
            tools: [ToolDefinition.Parse(
                "enum_text",
                "Enum text.",
                Closed("\"value\":{\"type\":\"string\",\"enum\":[\"long\"]}"))],
            limits: ToolEnvelopeLimits.Constrained with { MaxEnumTextCharacters = 3 });
        var rulesAct = () => TestCatalog.Plan(
            tools: [ToolDefinition.Parse("rules", "Rules.", Closed(manyProperties))],
            limits: ToolEnvelopeLimits.Constrained with { MaxSchemaRules = 3 });

        depthAct.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(d => d.Code == ToolEnvelopePlanDiagnosticCode.SchemaTooDeep);
        propertyAct.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(d => d.Code == ToolEnvelopePlanDiagnosticCode.TooManyProperties);
        enumAct.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(d => d.Code == ToolEnvelopePlanDiagnosticCode.TooManyEnumValues);
        enumTextAct.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(d => d.Code == ToolEnvelopePlanDiagnosticCode.EnumTextTooLong);
        rulesAct.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(d => d.Code == ToolEnvelopePlanDiagnosticCode.TooManySchemaRules);
    }

    [Test]
    public void Compile_RejectsDescriptionsThatExceedOrBreakPromptDataLines()
    {
        var tooLong = Closed("\"x\":{\"type\":\"string\",\"description\":\"12345\"}");
        var control = Closed("\"x\":{\"type\":\"string\",\"description\":\"line\\nbreak\"}");
        var limits = ToolEnvelopeLimits.Constrained with { MaxParameterDescriptionCharacters = 4 };

        var longAct = () => TestCatalog.Plan(
            tools: [ToolDefinition.Parse("long", "Long.", tooLong)],
            limits: limits);
        var controlAct = () => TestCatalog.Plan(
            tools: [ToolDefinition.Parse("control", "Control.", control)]);

        longAct.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(d => d.Code == ToolEnvelopePlanDiagnosticCode.DescriptionTooLong);
        controlAct.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(d => d.Code == ToolEnvelopePlanDiagnosticCode.InvalidSchema);
    }

    [Test]
    public void Compile_ChargesAReusedReferenceAtEveryInstanceDepth()
    {
        const string schema =
            """
            {
              "type":"object",
              "$defs":{
                "leaf":{
                  "type":"object",
                  "properties":{"value":{"type":"string"}},
                  "additionalProperties":false
                }
              },
              "properties":{
                "shallow":{"$ref":"#/$defs/leaf"},
                "wrapper":{
                  "type":"object",
                  "properties":{
                    "inner":{
                      "type":"object",
                      "properties":{"deep":{"$ref":"#/$defs/leaf"}},
                      "additionalProperties":false
                    }
                  },
                  "additionalProperties":false
                }
              },
              "additionalProperties":false
            }
            """;

        var act = () => TestCatalog.Plan(
            tools: [ToolDefinition.Parse("depth", "Tests reference depth.", schema)],
            limits: ToolEnvelopeLimits.Constrained with { MaxSchemaDepth = 4 });

        act.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == ToolEnvelopePlanDiagnosticCode.SchemaTooDeep
                && diagnostic.JsonPointer == "/properties/wrapper/properties/inner/properties/deep/$ref");
    }

    [Test]
    public void Compile_IntersectsEnumsWithBoundsBeforeBuildingGrammar()
    {
        var schema = Closed(
            "\"label\":{\"type\":\"string\",\"minLength\":2,\"maxLength\":3,\"enum\":[\"x\",\"ok\",\"long\"]},"
            + "\"score\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":3,\"enum\":[0,2,4]}");
        var turn = TestCatalog.Turn(
            ToolChoice.Required,
            tools: [ToolDefinition.Parse("bounded", "Uses bounded enums.", schema)]);

        turn.Grammar.Should().Contain("\"\\\"ok\\\"\"")
            .And.Contain("\"2\"")
            .And.NotContain("\"\\\"x\\\"\"")
            .And.NotContain("\"\\\"long\\\"\"")
            .And.NotContain("\"0\" | \"2\" | \"4\"");
        turn.Parse(TestCatalog.ToolRequest("bounded", """{"label":"ok","score":2}"""))
            .Should().BeOfType<ToolEnvelopeOutcome.ToolRequest>();
        turn.TryParse(
            TestCatalog.ToolRequest("bounded", """{"label":"x","score":2}"""),
            out _,
            out var error).Should().BeFalse();
        error!.Code.Should().Be(ToolEnvelopeErrorCode.SchemaViolation);
    }

    [TestCase("\"value\":{\"type\":\"string\",\"minLength\":2,\"enum\":[\"x\"]}")]
    [TestCase("\"value\":{\"type\":\"integer\",\"minimum\":2,\"enum\":[1]}")]
    [TestCase("\"value\":{\"type\":\"string\",\"maxLength\":1,\"const\":\"long\"}")]
    [TestCase("\"value\":{\"type\":\"number\",\"maximum\":1,\"const\":2}")]
    public void Compile_RejectsUnsatisfiablePrimitiveLiterals(string propertySchema)
    {
        var act = () => TestCatalog.Plan(
            tools:
            [
                ToolDefinition.Parse(
                    "unsatisfiable",
                    "Rejects impossible literal constraints.",
                    Closed(propertySchema)),
            ]);

        act.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == ToolEnvelopePlanDiagnosticCode.InvalidConstraint);
    }

    [TestCase("\"value\":{\"type\":\"number\",\"enum\":[1e100]}")]
    [TestCase("\"value\":{\"type\":\"number\",\"const\":1e100}")]
    public void Compile_RejectsNumericLiteralsOutsideTheDecimalProfile(string propertySchema)
    {
        var act = () => TestCatalog.Plan(
            tools:
            [
                ToolDefinition.Parse(
                    "decimal_only",
                    "Uses decimal-valued numbers.",
                    Closed(propertySchema)),
            ]);

        act.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == ToolEnvelopePlanDiagnosticCode.InvalidConstraint);
    }

    [Test]
    public void Compile_RejectsSchemasBeforeWalkingPastTheConfiguredCharacterBudget()
    {
        var schema = Closed(
            "\"value\":{\"type\":\"string\",\"description\":\""
            + new string('x', 200)
            + "\"}");
        var act = () => TestCatalog.Plan(
            tools: [ToolDefinition.Parse("large", "Large schema.", schema)],
            limits: ToolEnvelopeLimits.Constrained with
            {
                MaxToolSchemaCharacters = 100,
                MaxParameterDescriptionCharacters = 1_000,
            });

        var exception = act.Should().Throw<ToolEnvelopePlanException>().Which;
        exception.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == ToolEnvelopePlanDiagnosticCode.SchemaTooLarge
            && diagnostic.ToolName == "large");
    }

    [TestCase("\"value\":{\"type\":\"string\",\"enum\":[\"x\",\"x\"]}")]
    [TestCase("\"value\":{\"type\":\"number\",\"enum\":[1,1.0]}")]
    [TestCase("\"value\":{\"type\":\"boolean\",\"enum\":[true,true]}")]
    [TestCase("\"value\":{\"type\":\"null\",\"enum\":[null,null]}")]
    public void Compile_RejectsSemanticallyDuplicateEnumValues(string propertySchema)
    {
        var compile = () => TestCatalog.Plan(
            tools:
            [
                ToolDefinition.Parse(
                    "duplicates",
                    "Rejects duplicate literals.",
                    Closed(propertySchema)),
            ]);

        compile.Should().Throw<ToolEnvelopePlanException>()
            .Which.Diagnostics.Should().ContainSingle(diagnostic =>
                diagnostic.Code == ToolEnvelopePlanDiagnosticCode.InvalidConstraint
                && diagnostic.JsonPointer == "/properties/value/enum/1"
                && diagnostic.Message.Contains("index 0", StringComparison.Ordinal));
    }

    private static TestCaseData Invalid(
        string schema,
        ToolEnvelopePlanDiagnosticCode code,
        string pointer,
        string name) =>
        new TestCaseData(schema, code, pointer).SetName($"Rejects_{name.Replace(' ', '_')}");

    private static string Closed(string properties) =>
        $"{{\"type\":\"object\",\"properties\":{{{properties}}},\"additionalProperties\":false}}";
}
