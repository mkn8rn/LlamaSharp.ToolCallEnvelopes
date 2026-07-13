using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LlamaSharp.ToolCallEnvelopes.Tests;

[TestFixture]
public sealed partial class RepositoryDocumentationTests
{
    [Test]
    public void PublicDocumentation_ContainsOnlyCurrentDescriptiveLanguageAndParagraphs()
    {
        foreach (var path in PublicDocumentation())
        {
            var text = File.ReadAllText(path);
            text.Should().NotMatchRegex("(?i)\\blegacy\\b", path);

            var inCodeFence = false;
            foreach (var line in File.ReadLines(path))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inCodeFence = !inCodeFence;
                    continue;
                }

                if (inCodeFence)
                    continue;
                line.Should().NotMatchRegex("^\\s*(?:[-*+]\\s|\\d+[.)]\\s)", path);
                if (!line.Contains("](http", StringComparison.Ordinal))
                    line.Length.Should().BeLessThanOrEqualTo(100, path);
            }

            inCodeFence.Should().BeFalse($"{path} must close every code fence");
        }
    }

    [Test]
    public void EveryRelativeDocumentationLink_ResolvesToARealRepositoryFile()
    {
        foreach (var sourcePath in PublicDocumentation())
        {
            var text = File.ReadAllText(sourcePath);
            foreach (Match match in MarkdownLink().Matches(text))
            {
                var target = match.Groups["target"].Value;
                if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var pathOnly = target.Split('#')[0];
                var resolved = Path.GetFullPath(
                    Path.Combine(Path.GetDirectoryName(sourcePath)!, pathOnly));
                File.Exists(resolved).Should().BeTrue(
                    $"link '{target}' in {sourcePath} must resolve inside the repository");
            }
        }
    }

    [Test]
    public void EveryProjectAndPublicInstallExample_TargetsTheSingleCurrentReleaseContract()
    {
        var root = RepositoryRoot();
        var projects = Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToArray();

        foreach (var project in projects)
        {
            var document = XDocument.Load(project);
            document.Descendants("TargetFrameworks").Should().BeEmpty(project);
            document.Descendants("TargetFramework")
                .Select(element => element.Value)
                .Should().ContainSingle()
                .Which.Should().Be("net10.0", project);
        }

        var packageProject = XDocument.Load(Path.Combine(
            root,
            "LlamaSharp.ToolCallEnvelopes",
            "LlamaSharp.ToolCallEnvelopes.csproj"));
        packageProject.Descendants("Version").Should().ContainSingle()
            .Which.Value.Should().Be("0.2.0");
        File.ReadAllText(Path.Combine(root, "README.md"))
            .Should().Contain(
                "dotnet add package Supprocom.LlamaSharp.ToolCallEnvelopes --version 0.2.0");
    }

    private static string[] PublicDocumentation()
    {
        var root = RepositoryRoot();
        return new[] { Path.Combine(root, "README.md") }
            .Concat(Directory.EnumerateFiles(
                Path.Combine(root, "docs"),
                "*.md",
                SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LlamaSharp.ToolCallEnvelopes.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"The documentation tests started from '{AppContext.BaseDirectory}' but could not "
            + "find the repository root containing LlamaSharp.ToolCallEnvelopes.slnx. Run the "
            + "tests from a repository checkout that includes the public documentation.");
    }

    [GeneratedRegex(@"(?<!!)\[[^\]]+\]\((?<target>[^)]+)\)")]
    private static partial Regex MarkdownLink();
}
