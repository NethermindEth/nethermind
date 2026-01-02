// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Nethermind.Logging;

namespace NethCat.Discv5;

internal static class Discv5CommonOptions
{
    public static Option<LogLevel> CreateLogLevelOption() => new("--loglevel", "-l")
    {
        Description = "Log level (Trace, Debug, Info, Warn, Error)",
        HelpName = "level",
        DefaultValueFactory = _ => LogLevel.Info
    };

    public static Option<int> CreatePortOption() => new("--port", "-p")
    {
        Description = "UDP port for discovery",
        HelpName = "port",
        DefaultValueFactory = _ => 30303
    };

    public static Option<string?> CreatePrivateKeyOption() => new("--privatekey", "-k")
    {
        Description = "Private key hex (generates random if not specified)",
        HelpName = "hex",
        DefaultValueFactory = _ => "0x000000000000000000000000000000000000000000000000000000000000002a"
    };
}
