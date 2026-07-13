using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LlamaSharp.ToolCallEnvelopes.Internal;

internal static class SemanticPromptBuilder
{
    internal static string BuildCatalog(IReadOnlyList<CompiledTool> tools)
    {
        if (tools.Count == 0)
            return string.Empty;

        var output = new StringBuilder("TOOLS_DATA\n");
        foreach (var tool in tools)
        {
            output.Append("tool ")
                .Append(CanonicalJson.SerializeString(tool.Definition.Name))
                .Append(": ")
                .Append(CanonicalJson.SerializeString(tool.Definition.Description))
                .AppendLine();
            AppendProperties(output, tool.Arguments, path: "$", ancestorsRequired: true);
        }

        return output.Append("END_TOOLS_DATA").ToString();
    }

    internal static string BuildOutputContract(
        ToolChoice choice,
        IReadOnlyList<CompiledTool> tools,
        ToolEnvelopePlanOptions options)
    {
        var allowsAssistant = choice.Kind is ToolChoiceKind.Auto or ToolChoiceKind.None;
        var allowsTools =
            (choice.Kind is ToolChoiceKind.Auto or ToolChoiceKind.Required or ToolChoiceKind.Named)
            && tools.Count > 0;
        var output = new StringBuilder()
            .AppendLine("OUTPUT_CONTRACT")
            .AppendLine("Return exactly one compact JSON object and no surrounding text.");

        if (allowsAssistant)
            output.AppendLine("Final answer: {\"text\":\"...\"}");

        if (allowsTools)
        {
            var name = choice.Kind == ToolChoiceKind.Named
                ? choice.ToolName!
                : tools[0].Definition.Name;
            output.Append("Tool request: {\"tool_calls\":[{\"name\":")
                .Append(JsonSerializer.Serialize(name))
                .AppendLine(",\"arguments\":{...}}]}");
            output.AppendLine("Use one listed tool name and only that tool's listed arguments.");
            if (options.MaxCallsPerTurn > 1)
            {
                output.Append("Use between 1 and ")
                    .Append(options.MaxCallsPerTurn)
                    .AppendLine(" tool calls in one request.");
            }
        }

        if (allowsAssistant && options.AllowRefusal)
            output.AppendLine("Refusal: {\"refusal\":\"...\"}");

        return output.Append("END_OUTPUT_CONTRACT").ToString();
    }

    internal static string BuildSystemMessage(
        string systemPrompt,
        string catalog,
        string outputContract)
    {
        var output = new StringBuilder(systemPrompt.Trim());
        if (output.Length > 0)
            output.AppendLine().AppendLine();
        if (catalog.Length > 0)
            output.AppendLine(catalog).AppendLine().AppendLine();
        output.Append(outputContract);
        return output.ToString();
    }

    private static void AppendProperties(
        StringBuilder output,
        SchemaNode node,
        string path,
        bool ancestorsRequired)
    {
        if (node.Kind != SchemaKind.Object)
            return;

        foreach (var property in node.Properties)
        {
            var propertyPath = $"{path}[{CanonicalJson.SerializeString(property.Name)}]";
            var requirement = property.IsRequired
                ? ancestorsRequired ? "required" : "required when its parent is present"
                : "optional";
            AppendNode(output, "arg", propertyPath, property.Schema, requirement);

            var descendantsRequired = ancestorsRequired && property.IsRequired;
            if (property.Schema.Kind == SchemaKind.Array)
            {
                AppendArrayItems(
                    output,
                    property.Schema,
                    $"{propertyPath}[]",
                    descendantsRequired);
            }
            else
            {
                AppendProperties(
                    output,
                    property.Schema,
                    propertyPath,
                    descendantsRequired);
            }
        }
    }

    private static void AppendArrayItems(
        StringBuilder output,
        SchemaNode array,
        string itemPath,
        bool ancestorsRequired)
    {
        var item = array.Items!;
        AppendNode(output, "item", itemPath, item, requirement: null);
        if (item.Kind == SchemaKind.Array)
        {
            AppendArrayItems(output, item, $"{itemPath}[]", ancestorsRequired);
        }
        else
        {
            AppendProperties(output, item, itemPath, ancestorsRequired);
        }
    }

    private static void AppendNode(
        StringBuilder output,
        string label,
        string path,
        SchemaNode node,
        string? requirement)
    {
        output.Append(label).Append(' ')
            .Append(path)
            .Append(": ")
            .Append(NodeKind(node));

        if (requirement is not null)
            output.Append(", ").Append(requirement);

        if (node.Constant is { } constant)
            output.Append(", exactly ").Append(constant.GetRawText());
        else if (node.EnumValues.Count > 0)
            output.Append(", one of [").AppendJoin(",", node.EnumValues.Select(value => value.GetRawText())).Append(']');

        switch (node.Kind)
        {
            case SchemaKind.String:
                output.Append(", length ")
                    .Append(node.MinimumLength)
                    .Append("..")
                    .Append(node.MaximumLength);
                break;
            case SchemaKind.Array:
                output.Append(", items ")
                    .Append(node.MinimumItems)
                    .Append("..")
                    .Append(node.MaximumItems);
                break;
            case SchemaKind.Integer:
            case SchemaKind.Number:
                if (node.Minimum is not null)
                    output.Append(", min ").Append(node.Minimum.Value.ToString(CultureInfo.InvariantCulture));
                if (node.Maximum is not null)
                    output.Append(", max ").Append(node.Maximum.Value.ToString(CultureInfo.InvariantCulture));
                break;
        }

        if (!string.IsNullOrWhiteSpace(node.Description))
            output.Append("; ").Append(CanonicalJson.SerializeString(node.Description));
        output.AppendLine();
    }

    private static string NodeKind(SchemaNode node) =>
        node.Kind == SchemaKind.Array && node.Items is { } items
            ? $"array of {NodeKind(items)}"
            : SchemaKinds.JsonName(node.Kind);
}
