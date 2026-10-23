#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include SyncSettings.Helpers.cs

var key = "";
var superchain = false;
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--superchain") superchain = true;
    else if (args[index] is "-k" or "--key" && index + 1 < args.Length) key = args[++index];
    else
    {
        Console.WriteLine("usage: SyncSettings.cs [-k|--key API_KEY] [--superchain]");
        return args[index] is "-h" or "--help" ? 0 : 2;
    }
}
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
return await SyncSettings.Update(http, key, superchain, "./src/Nethermind/Nethermind.Runner/configs", Task.Delay);
