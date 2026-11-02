#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Xml.Linq;

var root = new DirectoryInfo(Environment.CurrentDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/Nethermind/Nethermind.slnx")))
    root = root.Parent;
var directory = Path.Combine(root?.FullName ?? throw new DirectoryNotFoundException("Run from within the Nethermind checkout."), "src/Nethermind");
var projects = XDocument.Load(Path.Combine(directory, "Nethermind.slnx")).Descendants("Project")
    .Select(node => Path.GetFullPath((string)node.Attribute("Path")!, directory))
    .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
var failed = false;
foreach (var project in projects.Order(StringComparer.Ordinal))
{
    foreach (var reference in XDocument.Load(project).Descendants("ProjectReference"))
    {
        var include = (string?)reference.Attribute("Include");
        if (string.IsNullOrEmpty(include) || include.Contains('$'))
            continue;
        var dependency = Path.GetFullPath(include.Replace('\\', '/'), Path.GetDirectoryName(project)!);
        if (!projects.Contains(dependency))
        {
            Console.Error.WriteLine($"{Path.GetFileName(project)} references {Path.GetFileName(dependency)} outside the solution; include it so Visual Studio can load and restore it.");
            failed = true;
        }
    }
}
if (failed) return 1;
Console.WriteLine("PASS main_solution_includes_literal_project_references\nPassed 1 tests.");
return 0;
