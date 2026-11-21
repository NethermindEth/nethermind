// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Xml.Linq;

static class EezChecks
{
    internal static Dictionary<string, string[]> Projects(string root)
    {
        var directory = Path.Combine(root, "src/Nethermind");
        return XDocument.Load(Path.Combine(directory, "Nethermind.slnx")).Descendants("Project")
            .Select(node => Path.GetFullPath(((string)node.Attribute("Path")!).Replace('\\', '/'), directory))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(path => Relative(root, path), path => XDocument.Load(path).Descendants("ProjectReference")
                .Select(node => (string?)node.Attribute("Include") ?? throw new InvalidDataException("Missing project reference."))
                .Select(reference => reference.Contains('$')
                    ? throw new InvalidDataException($"Cannot classify dynamic project reference in {path}.")
                    : Relative(root, Path.GetFullPath(reference.Replace('\\', '/'), Path.GetDirectoryName(path)!)))
                .ToArray(), StringComparer.Ordinal);
    }

    internal static void ValidateDependencies(Dictionary<string, string[]> projects)
    {
        foreach (var (path, references) in projects)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            foreach (var reference in references)
            {
                var dependency = Path.GetFileNameWithoutExtension(reference);
                var invalid = name == "Nethermind.Eez.Prove" ||
                    name == "Nethermind.Eez.Execution" && dependency.StartsWith("Nethermind.Eez", StringComparison.Ordinal) ||
                    name == "Nethermind.Eez.Attester" && dependency == "Nethermind.Eez" ||
                    !name.StartsWith("Nethermind.Eez", StringComparison.Ordinal) && name != "Nethermind.Runner" &&
                    !name.Contains(".Test", StringComparison.Ordinal) && !name.Contains("Benchmark", StringComparison.Ordinal) &&
                    dependency.StartsWith("Nethermind.Eez", StringComparison.Ordinal);
                if (invalid) throw new InvalidDataException($"Invalid dependency: {name} -> {dependency}.");
            }
        }
    }

    internal static CheckPlan Select(Dictionary<string, string[]> projects, IEnumerable<string> paths, bool full)
    {
        var dependencies = Dependencies(projects, ["Nethermind.Eez", "Nethermind.Eez.Attester"]);
        var testDependencies = Dependencies(projects, ["Nethermind.Eez.Test"]);
        var unit = full;
        var streaming = full;
        var live = full ? "full" : "none";
        foreach (var path in paths)
        {
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
            var directory = path[..Math.Max(0, path.LastIndexOf('/'))];
            if (path.StartsWith("scripts/eez/", StringComparison.Ordinal) ||
                path.StartsWith(".github/workflows/eez-", StringComparison.Ordinal) ||
                path.StartsWith(".github/actions/setup-dotnet/", StringComparison.Ordinal) ||
                (directory is "" or "src/Nethermind") && Path.GetFileName(path).StartsWith("Directory.", StringComparison.Ordinal) ||
                path.StartsWith("src/Nethermind/Nethermind.Analyzers/", StringComparison.Ordinal) ||
                path.StartsWith("src/Nethermind/Nethermind.JsonRpc.SourceGenerator", StringComparison.Ordinal) ||
                path is "global.json" or "nuget.config" or "Dockerfile" or "scripts/entrypoint.sh")
            {
                unit = streaming = true;
                live = "full";
                continue;
            }
            if (path.StartsWith("src/Nethermind/Nethermind.Eez.Test/", StringComparison.Ordinal) ||
                path.StartsWith("src/Nethermind/Nethermind.Test.Analyzers", StringComparison.Ordinal) ||
                path == "src/Nethermind/tests.props")
            {
                unit = streaming = true;
                continue;
            }
            if (path.StartsWith("src/Nethermind/Nethermind.Eez", StringComparison.Ordinal))
            {
                unit = streaming = true;
                live = "full";
                continue;
            }
            if (path == "src/Nethermind/Nethermind.slnx") unit = true;
            if (Contains(dependencies, path) || path.StartsWith("src/Nethermind/Nethermind.Runner/", StringComparison.Ordinal))
            {
                unit = streaming = true;
                if (live == "none") live = "smoke";
            }
            else if (Contains(testDependencies, path)) unit = streaming = true;
        }
        return new(unit, streaming, live);
    }

    private static bool Contains(HashSet<string> projects, string path) =>
        projects.Any(project => path.StartsWith(project[..(project.LastIndexOf('/') + 1)], StringComparison.Ordinal));

    private static HashSet<string> Dependencies(Dictionary<string, string[]> projects, string[] roots)
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(roots.Select(name => projects.Keys.Single(path => Path.GetFileNameWithoutExtension(path) == name)));
        while (pending.TryPop(out var path))
        {
            if (!dependencies.Add(path)) continue;
            if (!projects.TryGetValue(path, out var references))
                throw new InvalidDataException($"Project missing from solution: {path}.");
            foreach (var reference in references) pending.Push(reference);
        }
        return dependencies;
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}

record CheckPlan(bool Unit, bool Streaming, string Live);
