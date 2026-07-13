using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed class PublicApiContractTests
{
    private const string ExpectedSha256 =
        "977DD5B640021FE534AE08BE5675C8AC52DC704DF80FB68E92D24F77610D9488";

    [Test]
    public void PublicApiSurface_IsDeliberateAndStable()
    {
        var actual = DescribePublicSurface();
        var description = string.Join('\n', actual);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description)));
        if (!string.Equals(actualSha256, ExpectedSha256, StringComparison.Ordinal))
        {
            TestContext.Progress.WriteLine($"PUBLIC_API_SHA256={actualSha256}");
            TestContext.Progress.WriteLine(string.Join(Environment.NewLine, actual));
        }

        actualSha256.Should().Be(ExpectedSha256);
    }

    private static string[] DescribePublicSurface()
    {
        var assembly = typeof(ToolEnvelopePlan).Assembly;
        var output = new List<string>();
        foreach (var type in assembly.GetExportedTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            output.Add($"{TypeKind(type)} {DisplayType(type)}");
            if (type.IsEnum)
            {
                output.AddRange(Enum.GetNames(type).Select(name => $"  value {name}"));
                continue;
            }

            if (type.IsSubclassOf(typeof(MulticastDelegate)))
            {
                output.Add($"  invoke {DisplayMethod(type.GetMethod("Invoke")!)}");
                continue;
            }

            output.AddRange(type
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(constructor => constructor.ToString(), StringComparer.Ordinal)
                .Select(constructor => $"  constructor {DisplayParameters(constructor.GetParameters())}"));
            output.AddRange(type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                               | BindingFlags.DeclaredOnly)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property =>
                    $"  property {(IsStatic(property) ? "static " : string.Empty)}"
                    + $"{DisplayType(property.PropertyType)} {property.Name} "
                    + PropertyAccess(property)));
            output.AddRange(type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                            | BindingFlags.DeclaredOnly)
                .Where(IsIntentionalMethod)
                .OrderBy(method => method.Name, StringComparer.Ordinal)
                .ThenBy(method => method.ToString(), StringComparer.Ordinal)
                .Select(method => $"  method {DisplayMethod(method)}"));
        }

        return output.ToArray();
    }

    private static bool IsIntentionalMethod(MethodInfo method) =>
        method.Name.StartsWith("op_", StringComparison.Ordinal)
        || (!method.IsSpecialName
            && method.GetCustomAttribute<CompilerGeneratedAttribute>() is null
            && method.Name is not (
                nameof(object.Equals) or nameof(object.GetHashCode) or nameof(ToString)));

    private static string DisplayMethod(MethodInfo method) =>
        $"{(method.IsStatic ? "static " : string.Empty)}{DisplayType(method.ReturnType)} "
        + $"{method.Name}{DisplayParameters(method.GetParameters())}";

    private static string DisplayParameters(IReadOnlyList<ParameterInfo> parameters) =>
        "(" + string.Join(", ", parameters.Select(parameter =>
            $"{DisplayType(parameter.ParameterType)} {parameter.Name}"
            + (parameter.HasDefaultValue ? " = default" : string.Empty))) + ")";

    private static string PropertyAccess(PropertyInfo property)
    {
        var accessors = new List<string>();
        if (property.GetMethod is not null)
            accessors.Add("get");
        if (property.SetMethod is { } setter)
        {
            var init = setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .Contains(typeof(IsExternalInit));
            accessors.Add(init ? "init" : "set");
        }

        return $"{{ {string.Join("; ", accessors)}; }}";
    }

    private static string TypeKind(Type type) =>
        type.IsEnum ? "enum"
        : type.IsInterface ? "interface"
        : type.IsSubclassOf(typeof(MulticastDelegate)) ? "delegate"
        : type.IsAbstract && type.IsSealed ? "static class"
        : type.IsAbstract ? "abstract class"
        : type.IsValueType ? "struct"
        : type.IsSealed ? "sealed class"
        : "class";

    private static bool IsStatic(PropertyInfo property) =>
        (property.GetMethod ?? property.SetMethod)!.IsStatic;

    private static string DisplayType(Type type)
    {
        if (type == typeof(void))
            return "void";
        if (type == typeof(bool))
            return "bool";
        if (type == typeof(int))
            return "int";
        if (type == typeof(string))
            return "string";
        if (type.IsArray)
            return DisplayType(type.GetElementType()!) + "[]";
        if (!type.IsGenericType)
            return (type.FullName ?? type.Name).Replace('+', '.');

        var definitionName = (type.GetGenericTypeDefinition().FullName
                              ?? type.GetGenericTypeDefinition().Name)
            .Split('`')[0]
            .Replace('+', '.');
        return definitionName
               + "<"
               + string.Join(", ", type.GetGenericArguments().Select(DisplayType))
               + ">";
    }
}
