// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DocGen;

foreach (var arg in args)
{
    switch (arg.ToLower())
    {
        case "config":
            ConfigGenerator.Generate();
            break;
        case "jsonrpc":
            JsonRpcGenerator.Generate();
            break;
        case "metrics":
            MetricsGenerator.Generate();
            break;
        case "dbsize":
            DatabaseSizeGenerator.Generate();
            break;
        case "release":
            ConfigGenerator.Generate();
            JsonRpcGenerator.Generate();
            MetricsGenerator.Generate();
            break;
    }
}
