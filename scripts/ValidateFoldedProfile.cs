#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include Profiles.Helpers.cs

using System.Globalization;
using System.Text.RegularExpressions;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: ValidateFoldedProfile.cs <perf.folded>");
    return 2;
}
var path = args[0];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"error: folded profile '{path}' does not exist");
    return 1;
}
var managed = 0L;
var native = 0L;
var unknown = 0L;
foreach (var (stack, count) in Profiles.Read(path))
{
    if (count <= 0 || !StackRegex().IsMatch(stack)) continue;
    var leaf = stack[(stack.LastIndexOf(';') + 1)..];
    if (Profiles.IsManaged(leaf)) managed = checked(managed + count);
    else if (UnknownFrameRegex().IsMatch(leaf)) unknown = checked(unknown + count);
    else native = checked(native + count);
}
var total = managed + native + unknown;
if (total == 0)
{
    Console.Error.WriteLine($"error: folded profile '{path}' has no positive-sample stack");
    return 1;
}
Console.WriteLine($"Folded profile leaf/self samples: managed={managed} ({managed * 100.0 / total:F2}%), native={native} ({native * 100.0 / total:F2}%), unknown={unknown} ({unknown * 100.0 / total:F2}%)");
if (managed > 0) return 0;
Console.Error.WriteLine($"error: folded profile '{path}' has no managed leaf samples; check perf-map symbolization");
return 1;

partial class Program
{
    [GeneratedRegex(@"^[^\s;][^;]*;[^\s].*")]
    private static partial Regex StackRegex();
    [GeneratedRegex(@"^\[unknown\](\s|$)")]
    private static partial Regex UnknownFrameRegex();
}
