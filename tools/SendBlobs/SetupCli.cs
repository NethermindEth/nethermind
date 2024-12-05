// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Cli;
using Nethermind.Cli.Console;
using Nethermind.Consensus;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using System.CommandLine;

namespace SendBlobs;
internal static class SetupCli
{
    public static void SetupExecute(CliRootCommand command)
    {
        CliOption<string> rpcUrlOption = new("--rpcurl")
        {
            Description = "The URL of the JSON RPC server",
            HelpName = "URL",
            Required = true
        };
        CliOption<string> blobTxOption = new("--bloboptions")
        {
            Description = "Options in format '10x1-2', '2x5-5' etc. for the blobs",
            HelpName = "options"
        };
        CliOption<string> privateKeyOption = new("--privatekey")
        {
            Description = "The key to use for sending blobs",
            HelpName = "key"
        };
        CliOption<string> privateKeyFileOption = new("--keyfile")
        {
            Description = "File containing private keys that each blob tx will be send from",
            HelpName = "path"
        };
        CliOption<string> receiverOption = new("--receiveraddress")
        {
            Description = "Receiver address of the blobs",
            HelpName = "address",
            Required = true
        };
        CliOption<ulong?> maxFeePerDataGasOptionObsolete = new("--maxfeeperdatagas")
        {
            Description = "Set the maximum fee per blob data",
            HelpName = "fee"
        };
        CliOption<ulong?> maxFeePerBlobGasOption = new("--maxfeeperblobgas")
        {
            Description = "Set the maximum fee per blob data",
            HelpName = "fee"
        };
        CliOption<ulong> feeMultiplierOption = new("--feemultiplier")
        {
            DefaultValueFactory = r => 1UL,
            Description = "A multiplier to use for gas fees",
            HelpName = "value"
        };
        CliOption<ulong> maxPriorityFeeGasOption = new("--maxpriorityfee")
        {
            Description = "The maximum priority fee for each transaction",
            HelpName = "fee"
        };
        CliOption<bool> waitOption = new("--wait") { Description = "Wait for tx inclusion" };

        command.Add(rpcUrlOption);
        command.Add(blobTxOption);
        command.Add(privateKeyOption);
        command.Add(privateKeyFileOption);
        command.Add(receiverOption);
        command.Add(maxFeePerDataGasOptionObsolete);
        command.Add(maxFeePerBlobGasOption);
        command.Add(feeMultiplierOption);
        command.Add(maxPriorityFeeGasOption);
        command.Add(waitOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            PrivateKey[] privateKeys;

            string? privateKeyFileValue = parseResult.GetValue(privateKeyFileOption);
            string? privateKeyValue = parseResult.GetValue(privateKeyOption);

            if (privateKeyFileValue is not null)
                privateKeys = File.ReadAllLines(privateKeyFileValue).Select(k => new PrivateKey(k)).ToArray();
            else if (privateKeyValue is not null)
                privateKeys = [new PrivateKey(privateKeyValue)];
            else
            {
                Console.WriteLine("Missing private key argument.");
                return Task.CompletedTask;
            }

            BlobSender sender = new(parseResult.GetValue(rpcUrlOption)!, SimpleConsoleLogManager.Instance);

            return sender.SendRandomBlobs(
                ParseTxOptions(parseResult.GetValue(blobTxOption)),
                privateKeys,
                parseResult.GetValue(receiverOption)!,
                parseResult.GetValue(maxFeePerBlobGasOption) ?? parseResult.GetValue(maxFeePerDataGasOptionObsolete),
                parseResult.GetValue(feeMultiplierOption),
                parseResult.GetValue(maxPriorityFeeGasOption),
                parseResult.GetValue(waitOption));
        });
    }

    private static (int count, int blobCount, string @break)[] ParseTxOptions(string? options)
    {
        if (string.IsNullOrWhiteSpace(options))
            return Array.Empty<(int count, int blobCount, string @break)>();

        ReadOnlySpan<char> chars = options.AsSpan();
        var result = new List<(int, int, string)>();

        ReadOnlySpan<char> nextComma;
        int offSet = 0;
        while (true)
        {
            nextComma = SplitToNext(chars[offSet..], ',');

            ReadOnlySpan<char> @break = SplitToNext(nextComma, '-', true);
            ReadOnlySpan<char> rest = nextComma[..(nextComma.Length - (@break.Length == 0 ? 0 : @break.Length + 1))];
            ReadOnlySpan<char> count = SplitToNext(rest, 'x');
            ReadOnlySpan<char> txCount = SplitToNext(rest, 'x', true);

            result.Add(new(int.Parse(count), txCount.Length == 0 ? 1 : int.Parse(txCount), new string(@break)));

            offSet += nextComma.Length + 1;
            if (offSet > chars.Length)
            {
                break;
            }
        }
        return result.ToArray();
    }

    private static ReadOnlySpan<char> SplitToNext(ReadOnlySpan<char> line, char separator, bool returnRemainder = false)
    {
        int i = line.IndexOf(separator);
        if (i == -1)
            return returnRemainder ? ReadOnlySpan<char>.Empty : line;
        return returnRemainder ? line[(i + 1)..] : line[..i];
    }

    public static void SetupDistributeCommand(CliCommand root)
    {
        CliCommand command = new("distribute")
        {
            Description = "Distribute funds from an address to a number of new addresses"
        };
        CliOption<string> rpcUrlOption = new("--rpcurl")
        {
            Description = "The URL of the JSON RPC server",
            HelpName = "URL",
            Required = true
        };
        CliOption<string> privateKeyOption = new("--privatekey")
        {
            Description = "The private key to distribute funds from",
            HelpName = "key",
            Required = true,
        };
        CliOption<uint> keyNumberOption = new("--number")
        {
            Description = "The number of new addresses/keys to make",
            HelpName = "value"
        };
        CliOption<string> keyFileOption = new("--keyfile")
        {
            Description = "File where the newly generated keys are written",
            HelpName = "path"
        };
        CliOption<UInt256> maxPriorityFeeGasOption = new("--maxpriorityfee")
        {
            Description = "The maximum priority fee for each transaction",
            HelpName = "fee"
        };
        CliOption<UInt256> maxFeeOption = new("--maxfee")
        {
            Description = "The maxFeePerGas fee paid for each transaction",
            HelpName = "fee"
        };

        command.Add(rpcUrlOption);
        command.Add(privateKeyOption);
        command.Add(keyNumberOption);
        command.Add(keyFileOption);
        command.Add(maxPriorityFeeGasOption);
        command.Add(maxFeeOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            INodeManager nodeManager = InitNodeManager(
                parseResult.GetValue(rpcUrlOption)!, SimpleConsoleLogManager.Instance.GetClassLogger());

            string? chainIdString = await nodeManager.Post<string>("eth_chainId") ?? "1";
            ulong chainId = HexConvert.ToUInt64(chainIdString);

            Signer signer = new(chainId, new PrivateKey(parseResult.GetValue(privateKeyOption)!),
                SimpleConsoleLogManager.Instance);

            FundsDistributor distributor = new FundsDistributor(
                nodeManager, chainId, parseResult.GetValue(keyFileOption), SimpleConsoleLogManager.Instance);
            IEnumerable<string> hashes = await distributor.DitributeFunds(
                signer,
                parseResult.GetValue(keyNumberOption),
                parseResult.GetValue(maxFeeOption),
                parseResult.GetValue(maxPriorityFeeGasOption));
        });

        root.Add(command);
    }

    public static void SetupReclaimCommand(CliCommand root)
    {
        CliCommand command = new("reclaim")
        {
            Description = "Reclaim funds distributed from the 'distribute' command"
        };
        CliOption<string> rpcUrlOption = new("--rpcurl")
        {
            Description = "The URL of the JSON RPC server",
            HelpName = "URL",
            Required = true
        };
        CliOption<string> receiverOption = new("--receiveraddress")
        {
            Description = "The address to send the funds to",
            HelpName = "address",
            Required = true,
        };
        CliOption<string> keyFileOption = new("--keyfile")
        {
            Description = "File of the private keys to reclaim from",
            HelpName = "path"
        };
        CliOption<UInt256> maxPriorityFeeGasOption = new("--maxpriorityfee")
        {
            Description = "The maximum priority fee for each transaction",
            HelpName = "fee"
        };
        CliOption<UInt256> maxFeeOption = new("--maxfee")
        {
            Description = "The maxFeePerGas fee paid for each transaction",
            HelpName = "fee"
        };

        command.Add(rpcUrlOption);
        command.Add(keyFileOption);
        command.Add(maxPriorityFeeGasOption);
        command.Add(maxFeeOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            INodeManager nodeManager = InitNodeManager(parseResult.GetValue(rpcUrlOption)!, SimpleConsoleLogManager.Instance.GetClassLogger());

            string? chainIdString = await nodeManager.Post<string>("eth_chainId") ?? "1";
            ulong chainId = HexConvert.ToUInt64(chainIdString);

            FundsDistributor distributor = new(nodeManager, chainId, parseResult.GetValue(keyFileOption), SimpleConsoleLogManager.Instance);
            IEnumerable<string> hashes = await distributor.ReclaimFunds(
                new(parseResult.GetValue(receiverOption)!),
                parseResult.GetValue(maxFeeOption),
                parseResult.GetValue(maxPriorityFeeGasOption));
        });

        root.Add(command);
    }

    public static INodeManager InitNodeManager(string rpcUrl, ILogger logger)
    {
        ICliConsole cliConsole = new CliConsole();
        IJsonSerializer serializer = new EthereumJsonSerializer();
        OneLoggerLogManager logManager = new OneLoggerLogManager(logger);
        ICliEngine engine = new CliEngine(cliConsole);
        INodeManager nodeManager = new NodeManager(engine, serializer, cliConsole, logManager);
        nodeManager.SwitchUri(new Uri(rpcUrl));
        return nodeManager;
    }

    public static void SetupSendFileCommand(CliCommand root)
    {
        CliCommand command = new("send")
        {
            Description = "Sends a file"
        };
        CliOption<string> fileOption = new("--file")
        {
            Description = "File to send as is",
            HelpName = "path",
            Required = true
        };
        CliOption<string> rpcUrlOption = new("--rpcurl")
        {
            Description = "The URL of the JSON RPC server",
            HelpName = "URL",
            Required = true
        };
        CliOption<string> privateKeyOption = new("--privatekey")
        {
            Description = "The key to use for sending blobs",
            HelpName = "key",
            Required = true,
        };
        CliOption<string> receiverOption = new("--receiveraddress")
        {
            Description = "Receiver address of the blobs",
            HelpName = "address",
            Required = true,
        };
        CliOption<UInt256> maxFeePerBlobGasOption = new("--maxfeeperblobgas")
        {
            DefaultValueFactory = r => 1000,
            Description = "Set the maximum fee per blob data",
            HelpName = "fee"
        };
        CliOption<ulong> feeMultiplierOption = new("--feemultiplier")
        {
            DefaultValueFactory = r => 1UL,
            Description = "A multiplier to use for gas fees",
            HelpName = "value"
        };
        CliOption<UInt256?> maxPriorityFeeGasOption = new("--maxpriorityfee")
        {
            Description = "The maximum priority fee for each transaction",
            HelpName = "fee"
        };
        CliOption<bool> waitOption = new("--wait") { Description = "Wait for tx inclusion" };

        command.Add(fileOption);
        command.Add(rpcUrlOption);
        command.Add(privateKeyOption);
        command.Add(receiverOption);
        command.Add(maxFeePerBlobGasOption);
        command.Add(feeMultiplierOption);
        command.Add(maxPriorityFeeGasOption);
        command.Add(waitOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            PrivateKey privateKey = new(parseResult.GetValue(privateKeyOption)!);
            byte[] data = File.ReadAllBytes(parseResult.GetValue(fileOption)!);
            BlobSender sender = new(parseResult.GetValue(rpcUrlOption)!, SimpleConsoleLogManager.Instance);

            return sender.SendData(
                data,
                privateKey,
                parseResult.GetValue(receiverOption)!,
                parseResult.GetValue(maxFeePerBlobGasOption),
                parseResult.GetValue(feeMultiplierOption),
                parseResult.GetValue(maxPriorityFeeGasOption),
                parseResult.GetValue(waitOption));
        });

        root.Add(command);
    }
}
