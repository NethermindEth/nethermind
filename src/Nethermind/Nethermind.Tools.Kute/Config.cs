// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CommandLine;

namespace Nethermind.Tools.Kute;

class Config
{
    [Option('f', "file", Required = true, HelpText = "File containing JSON RPC messages")]
    public string MessagesFile { get; }

    [Option('h', "host", Required = false, Default = "http://localhost:8551", HelpText = "Host for JSON RPC calls")]
    public string Host { get; }

    [Option('s', "secret", Required = true, HelpText = "Path to file with hex encoded secret for jwt authentication")]
    public string JwtSecretFile { get; }

    [Option('d', "dry", Required = false, Default = false, HelpText = "Only log into console")]
    public bool DryRun { get; }

    public Config(string messagesFile, string host, string jwtSecretFile, bool dryRun)
    {
        MessagesFile = messagesFile;
        Host = host;
        JwtSecretFile = jwtSecretFile;
        DryRun = dryRun;
    }
}
