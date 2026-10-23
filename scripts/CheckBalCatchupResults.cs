#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include CheckBalCatchupResults.Helpers.cs

try
{
    BalCatchupResults.Check(args[0], int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
