// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Cli;
using Nethermind.Cli.Console;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Org.BouncyCastle.Utilities.Encoders;
using SendBlobs;

CommandLineApplication app = new() { Name = "SendBlobs" };

SetupCli.SetupExecute(app);
SetupCli.SetupDistributeCommand(app);
SetupCli.SetupReclaimCommand(app);

try
{
    app.Execute(args);
}
catch (CommandParsingException ex)
{
    Console.WriteLine(ex.Message);
    app.ShowHelp();
}





