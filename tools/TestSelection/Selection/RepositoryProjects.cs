// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;

namespace Nethermind.TestSelection.Selection;

/// <summary>Reads the projects of a checkout from its csproj files.</summary>
internal static partial class RepositoryProjects
{
    /// <summary>The changed-file marker for a diff too large to list: it touches every test.</summary>
    public const string UnknownChanges = "*";

    public static Projects Read(string root)
    {
        Dictionary<string, IReadOnlySet<string>> references = [];
        Dictionary<string, HashSet<string>> includes = [];
        foreach (string directory in Directory.EnumerateDirectories(Path.Combine(root, Projects.SourceRoot)))
        {
            string? csproj = Directory.EnumerateFiles(directory, "*.csproj").FirstOrDefault();
            if (csproj is null)
            {
                continue;
            }

            string project = Relative(root, directory);
            string text = File.ReadAllText(csproj);
            references[project] = ProjectReferences(root, directory, text);
            foreach (string path in IncludedPaths(root, directory, text))
            {
                if (!includes.TryGetValue(path, out HashSet<string>? owners))
                {
                    includes[path] = owners = [];
                }

                owners.Add(project);
            }
        }

        return new Projects(references, includes.ToDictionary(static pair => pair.Key, static pair => (IReadOnlySet<string>)pair.Value));
    }

    private static HashSet<string> ProjectReferences(string root, string directory, string csproj)
    {
        HashSet<string> references = [];
        foreach (Match match in ProjectReference().Matches(csproj))
        {
            string path = Path.GetFullPath(Path.Combine(directory, Normalize(match.Groups[1].Value)));
            references.Add(Relative(root, Path.GetDirectoryName(path)!));
        }

        return references;
    }

    /// <summary>Paths outside the project that it builds from: each included file, or the directory above a wildcard.</summary>
    internal static HashSet<string> IncludedPaths(string root, string directory, string csproj)
    {
        HashSet<string> paths = [];
        foreach (Match match in ItemInclude().Matches(csproj))
        {
            string include = Normalize(match.Groups[1].Value);
            string fixedPart = include.Split('*')[0].TrimEnd('/');
            if (!fixedPart.StartsWith("..", StringComparison.Ordinal))
            {
                continue;
            }

            string path = Relative(root, Path.GetFullPath(Path.Combine(directory, fixedPart)));
            if (path.Length > 0 && !path.StartsWith("..", StringComparison.Ordinal))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    [GeneratedRegex(@"ProjectReference\s+Include=""([^""]+)""")]
    private static partial Regex ProjectReference();

    [GeneratedRegex(@"<(?:Compile|Content|None|EmbeddedResource)\s+Include=""([^""]+)""")]
    private static partial Regex ItemInclude();
}
