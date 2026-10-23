#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include Version.Helpers.cs
#:include ci/ScriptProcess.Helpers.cs
using System.Globalization;
using System.Text.RegularExpressions;

if (args is ["--test"])
    return (await ScriptProcess.Run("dotnet", ["run", "--file", Path.Combine(ScriptVersion.Root(), "scripts/ci/TestBumpVersion.cs")], CancellationToken.None, echo: true)).Code;
if (args.Length < 2 || args[0].Length == 0 || args[1].Length == 0)
{
    Console.WriteLine("Usage: BumpVersion.cs <branch-name> <props-path> | --test");
    return 1;
}
try
{
    var branch = ReleaseBranch().Match(args[0]);
    if (!branch.Success) throw new InvalidOperationException("error: Invalid branch name format. Expected: refs/heads/release/X.Y.0");
    if (!File.Exists(args[1])) throw new InvalidOperationException($"error: Directory.Build.props not found at {args[1]}");
    var minor = branch.Groups[2].Value;
    // Preserve Bash arithmetic: a leading zero makes the minor version octal.
    var next = checked(Convert.ToInt64(minor, minor.StartsWith('0') ? 8 : 10) + 1);
    var version = $"{branch.Groups[1].Value}.{next.ToString(CultureInfo.InvariantCulture)}.0";
    var text = File.ReadAllText(args[1]);
    var current = ScriptVersion.Property(text, "VersionPrefix");
    var update = current != version;
    if (update) File.WriteAllText(args[1], VersionPrefix().Replace(text, $"<VersionPrefix>{version}</VersionPrefix>"));
    Console.WriteLine(update ? $"Updated version from {current} to {version}" : $"Version {version} is already set in {args[1]}");
    Console.WriteLine($"needs_update={update.ToString().ToLowerInvariant()}\ncurrent_version={current}\nnew_version={version}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

partial class Program
{
    [GeneratedRegex(@"\Arefs/heads/release/([0-9]+)\.([0-9]+)\.0\z")]
    private static partial Regex ReleaseBranch();
    [GeneratedRegex(@"<VersionPrefix>.*</VersionPrefix>")]
    private static partial Regex VersionPrefix();
}
