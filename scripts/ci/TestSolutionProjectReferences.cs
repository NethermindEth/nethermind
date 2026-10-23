#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Xml.Linq;

var passed = 0;
try
{
    AssertReferences(Path.Combine(GetRepositoryRoot(), "src/Nethermind/Nethermind.slnx"));
    Console.WriteLine("PASS main_solution_includes_literal_project_references");
    passed++;

    var fixture = Directory.CreateTempSubdirectory("solution-project-references-");
    try
    {
        var solution = Path.Combine(fixture.FullName, "Fixture.slnx");
        Directory.CreateDirectory(Path.Combine(fixture.FullName, "App"));
        Directory.CreateDirectory(Path.Combine(fixture.FullName, "Dependency"));
        File.WriteAllText(Path.Combine(fixture.FullName, "Dependency/Dependency.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(fixture.FullName, "App/App.csproj"), """
            <Project>
              <ItemGroup>
                <ProjectReference Include="..\Dependency\Dependency.csproj" />
                <ProjectReference Include="../Dependency/Dependency.csproj" />
                <ProjectReference Include="$(GeneratedProject)" />
                <ProjectReference Include="" />
                <ProjectReference />
              </ItemGroup>
            </Project>
            """);
        foreach (var includeDependency in new[] { true, false })
        {
            var document = new XDocument(new XElement("Solution",
                new XElement("Folder", new XElement("Project", new XAttribute("Path", "App/App.csproj")))));
            if (includeDependency)
                document.Root!.Add(new XElement("Project", new XAttribute("Path", "Dependency/Dependency.csproj")));
            document.Save(solution);
            var errors = FindMissingReferences(solution);
            var expected = includeDependency ? Array.Empty<string>() : new[]
            {
                "App.csproj references Dependency.csproj outside the solution; include it so Visual Studio can load and restore it.",
                "App.csproj references Dependency.csproj outside the solution; include it so Visual Studio can load and restore it."
            };
            if (!errors.SequenceEqual(expected))
                throw new InvalidOperationException($"Unexpected fixture errors (includeDependency={includeDependency}): {string.Join('\n', errors)}");
            Console.WriteLine($"PASS fixture_include_dependency_{includeDependency}");
            passed++;
        }
    }
    finally
    {
        fixture.Delete(recursive: true);
    }

    Console.WriteLine($"Passed {passed} tests.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static void AssertReferences(string solution)
{
    var errors = FindMissingReferences(solution);
    if (errors.Count > 0)
        throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
}

static List<string> FindMissingReferences(string solution)
{
    var projects = XDocument.Load(solution).Descendants("Project")
        .Select(node => Path.GetFullPath((string?)node.Attribute("Path")
            ?? throw new InvalidOperationException("Solution project is missing its Path attribute."), Path.GetDirectoryName(solution)!))
        .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    var errors = new List<string>();
    foreach (var project in projects.Order(StringComparer.Ordinal))
    {
        foreach (var reference in XDocument.Load(project).Descendants("ProjectReference"))
        {
            var include = (string?)reference.Attribute("Include");
            if (string.IsNullOrEmpty(include) || include.Contains('$'))
                continue;
            var dependency = Path.GetFullPath(include.Replace('\\', '/'), Path.GetDirectoryName(project)!);
            if (!projects.Contains(dependency))
                errors.Add($"{Path.GetFileName(project)} references {Path.GetFileName(dependency)} outside the solution; include it so Visual Studio can load and restore it.");
        }
    }
    return errors;
}

static string GetRepositoryRoot([CallerFilePath] string source = "") =>
    Path.GetFullPath("../..", Path.GetDirectoryName(source)!);
