// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace NethCat.Discv5;

/// <summary>
/// Connect to a single node and log all discv5 protocol messages.
/// </summary>
internal static class ConnectCommand
{
    public static void Setup(Command parent)
    {
        Command connectCommand = new("connect")
        {
            Description = "Connect to a single node and log all discv5 protocol messages"
        };

        Option<string> nodeOption = new("--node", "-n")
        {
            Description = "Target node ENR or enode to connect to",
            HelpName = "enr",
            Required = true
        };

        Option<int> portOption = Discv5CommonOptions.CreatePortOption();
        Option<string?> privateKeyOption = Discv5CommonOptions.CreatePrivateKeyOption();
        Option<LogLevel> logLevelOption = Discv5CommonOptions.CreateLogLevelOption();

        Option<int> durationOption = new("--duration", "-d")
        {
            Description = "Duration to stay connected in seconds (0 for infinite)",
            HelpName = "seconds",
            DefaultValueFactory = _ => 0
        };

        connectCommand.Add(nodeOption);
        connectCommand.Add(portOption);
        connectCommand.Add(privateKeyOption);
        connectCommand.Add(logLevelOption);
        connectCommand.Add(durationOption);

        connectCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            string nodeEnr = parseResult.GetValue(nodeOption)!;
            int port = parseResult.GetValue(portOption);
            string? privateKeyHex = parseResult.GetValue(privateKeyOption);
            LogLevel logLevel = parseResult.GetValue(logLevelOption);
            int duration = parseResult.GetValue(durationOption);

            SimpleConsoleLogManager logManager = new(logLevel);
            PrivateKey privateKey = Discv5Session.CreatePrivateKey(privateKeyHex);

            Console.WriteLine($"Node ID: {privateKey.PublicKey}");
            Console.WriteLine($"Starting discv5 on port {port}...");
            Console.WriteLine($"Connecting to: {nodeEnr}");
            Console.WriteLine($"Log level: {logLevel}");
            Console.WriteLine();

            await using Discv5Session session = await Discv5Session.CreateAndStartAsync(
                port,
                nodeEnr,
                privateKey,
                logManager);

            Console.WriteLine("Connected. Logging discv5 protocol messages...");
            Console.WriteLine("Press Ctrl+C to stop");
            Console.WriteLine();

            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (duration > 0)
            {
                cts.CancelAfter(TimeSpan.FromSeconds(duration));
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine($"Duration of {duration} seconds reached.");
            }

            Console.WriteLine("Disconnecting...");
            Console.WriteLine("Done.");
        });

        parent.Add(connectCommand);
    }
}
