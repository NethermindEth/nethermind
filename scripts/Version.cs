#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include Version.Helpers.cs

try
{
    Console.WriteLine(ScriptVersion.Read(args.Length > 0 && args[0].Length > 0 ? args[0] : Path.Combine(ScriptVersion.Root(), "src/Nethermind/Directory.Build.props")));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
