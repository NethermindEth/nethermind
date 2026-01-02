// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace NethCat.Discv5;

/// <summary>
/// Discover nodes via discv5 random walk.
/// </summary>
internal static class DiscoverCommand
{
    public static void Setup(Command parent)
    {
        Command discoverCommand = new("discover")
        {
            Description = "Discover nodes via discv5 random walk"
        };

        Option<string[]> bootnodesOption = new("--bootnodes", "-b")
        {
            Description = "Comma-separated list of bootnode ENRs or enodes",
            HelpName = "enrs",
            Required = true,
            AllowMultipleArgumentsPerToken = true
        };

        Option<int> portOption = Discv5CommonOptions.CreatePortOption();

        Option<int> timeoutOption = new("--timeout", "-t")
        {
            Description = "Discovery timeout in seconds (0 for infinite)",
            HelpName = "seconds",
            DefaultValueFactory = _ => 60
        };

        Option<string?> privateKeyOption = Discv5CommonOptions.CreatePrivateKeyOption();
        Option<LogLevel> logLevelOption = Discv5CommonOptions.CreateLogLevelOption();

        discoverCommand.Add(bootnodesOption);
        discoverCommand.Add(portOption);
        discoverCommand.Add(timeoutOption);
        discoverCommand.Add(privateKeyOption);
        discoverCommand.Add(logLevelOption);

        discoverCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            string[] bootnodes = parseResult.GetValue(bootnodesOption)!;
            int port = parseResult.GetValue(portOption);
            int timeout = parseResult.GetValue(timeoutOption);
            string? privateKeyHex = parseResult.GetValue(privateKeyOption);
            LogLevel logLevel = parseResult.GetValue(logLevelOption);

            string bootnodesString = string.Join(",", bootnodes);

            SimpleConsoleLogManager logManager = new(logLevel);
            PrivateKey privateKey = Discv5Session.CreatePrivateKey(privateKeyHex);

            Console.WriteLine($"Node ID: {privateKey.PublicKey}");
            Console.WriteLine($"Starting discv5 on port {port}...");
            Console.WriteLine($"Bootnodes: {bootnodesString}");
            Console.WriteLine();

            await using Discv5Session session = await Discv5Session.CreateAndStartAsync(
                port,
                bootnodesString,
                privateKey,
                logManager);

            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (timeout > 0)
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeout));
            }

            Console.WriteLine("Discovering nodes via random walk...");
            Console.WriteLine("Press Ctrl+C to stop");
            Console.WriteLine();

            int nodeCount = 0;
            try
            {
                await foreach (Node node in session.App.DiscoverNodes(cts.Token))
                {
                    nodeCount++;
                    Console.WriteLine($"[{nodeCount}] {node.Id} @ {node.Address}");
                    if (node.Enr is not null)
                    {
                        Console.WriteLine($"    ENR: {node.Enr}");
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine($"Discovery timeout reached after {timeout} seconds.");
            }

            Console.WriteLine();
            Console.WriteLine($"Total nodes discovered: {nodeCount}");
        });

        parent.Add(discoverCommand);
    }
}
