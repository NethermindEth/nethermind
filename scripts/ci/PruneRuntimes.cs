#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include PruneRuntimes.Helpers.cs

try
{
    return RuntimePruner.Run(args, Console.Out, Console.Error);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
