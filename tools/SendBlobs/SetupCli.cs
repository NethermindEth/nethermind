// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using System.CommandLine;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Nethermind.JsonRpc.Client;
using Nethermind.Serialization.Json;
using Nethermind.Specs;

namespace SendBlobs;
internal static class SetupCli
{
    public static void SetupExecute(RootCommand command)
    {
        Option<string> rpcUrlOption = new("--rpcurl")
        {
            Description = "The URL of the JSON RPC server",
            HelpName = "URL",
            Required = true
        };
        Option<string> blobTxOption = new("--bloboptions")
        {
            Description = "Options in format '10x1-2', '2x5-5' etc. for the blobs",
            HelpName = "options"
        };
        Option<string> privateKeyOption = new("--privatekey")
        {
            Description = "The key to use for sending blobs",
            HelpName = "key"
        };
        Option<string> privateKeyFileOption = new("--keyfile")
        {
            Description = "File containing private keys that each blob tx will be send from",
            HelpName = "path"
        };
        Option<string> receiverOption = new("--receiveraddress")
        {
            Description = "Receiver address of the blobs",
            HelpName = "address",
            Required = true
        };
        Option<ulong?> maxFeePerDataGasOptionObsolete = new("--maxfeeperdatagas")
        {
            Description = "Set the maximum fee per blob data",
            HelpName = "fee"
        };
        Option<ulong?> maxFeePerBlobGasOption = new("--maxfeeperblobgas")
        {
            Description = "Set the maximum fee per blob data",
            HelpName = "fee"
        };
        Option<ulong> feeMultiplierOption = new("--feemultiplier")
        {
            DefaultValueFactory = r => 1UL,
            Description = "A multiplier to use for gas fees",
            HelpName = "value"
        };
        Option<ulong> maxPriorityFeeGasOption = new("--maxpriorityfee")
        {
            Description = "The maximum priority fee for each transaction",
            HelpName = "fee"
        };
        Option<bool> waitOption = new("--wait") { Description = "Wait for tx inclusion" };
        Option<string> forkOption = new("--fork") { Description = "Specify fork for MaxBlobCount, TargetBlobCount" };

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
        command.Add(forkOption);
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

            string? fork = parseResult.GetValue(forkOption);
            IReleaseSpec spec = fork is null ? Prague.Instance : SpecNameParser.Parse(fork);

            BlobSender sender = new(parseResult.GetValue(rpcUrlOption)!, SimpleConsoleLogManager.Instance);
            return sender.SendRandomBlobs(
                ParseTxOptions(parseResult.GetValue(blobTxOption)),
                privateKeys,
                parseResult.GetValue(receiverOption)!,
                parseResult.GetValue(maxFeePerBlobGasOption) ?? parseResult.GetValue(maxFeePerDataGasOptionObsolete),
                parseResult.GetValue(feeMultiplierOption),
                parseResult.GetValue(maxPriorityFeeGasOption),
                parseResult.GetValue(waitOption),
                spec);
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

    public static void SetupDistributeCommand(Command root)
    {
        Command command = new("distribute")
        {
            Description = "Distribute funds from an address to a number of new addresses"
        };
        Option<string> rpcUrlOption = new("--rpcurl")
        {
            Description = "The URL of the JSON RPC server",
            HelpName = "URL",
            Required = true
        };
        Option<string> privateKeyOption = new("--privatekey")
        {
            Description = "The private key to distribute funds from",
            HelpName = "key",
            Required = true,
        };
        Option<uint> keyNumberOption = new("--number")
        {
            Description = "The number of new addresses/keys to make",
            HelpName = "value"
        };
        Option<string> keyFileOption = new("--keyfile")
        {
            Description = "File where the newly generated keys are written",
            HelpName = "path"
        };
        Option<UInt256> maxPriorityFeeGasOption = new("--maxpriorityfee")
        {
            Description = "The maximum priority fee for each transaction",
            HelpName = "fee"
        };
        Option<UInt256> maxFeeOption = new("--maxfee")
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
            IJsonRpcClient rpcClient = InitRpcClient(
                parseResult.GetValue(rpcUrlOption)!,
                SimpleConsoleLogManager.Instance.GetClassLogger());

            string? chainIdString = await rpcClient.Post<string>("eth_chainId") ?? "1";
            ulong chainId = HexConvert.ToUInt64(chainIdString);

            Signer signer = new(chainId, new PrivateKey(parseResult.GetValue(privateKeyOption)!),
                SimpleConsoleLogManager.Instance);

            FundsDistributor distributor = new(
                rpcClient, chainId, parseResult.GetValue(keyFileOption), SimpleConsoleLogManager.Instance);
            IEnumerable<string> hashes = await distributor.DitributeFunds(
                signer,
                parseResult.GetValue(keyNumberOption),
                parseResult.GetValue(maxFeeOption),
                parseResult.GetValue(maxPriorityFeeGasOption));
        });

        root.Add(command);
    }

    public static void SetupReclaimCommand(Command root)
    {
        Command command = new("reclaim")
        {
            Description = "Reclaim funds distributed from the 'distribute' command"
        };
        Option<string> rpcUrlOption = new("--rpcurl")
        {
            Description = "The URL of the JSON RPC server",
            HelpName = "URL",
            Required = true
        };
        Option<string> receiverOption = new("--receiveraddress")
        {
            Description = "The address to send the funds to",
            HelpName = "address",
            Required = true,
        };
        Option<string> keyFileOption = new("--keyfile")
        {
            Description = "File of the private keys to reclaim from",
            HelpName = "path"
        };
        Option<UInt256> maxPriorityFeeGasOption = new("--maxpriorityfee")
        {
            Description = "The maximum priority fee for each transaction",
            HelpName = "fee"
        };
        Option<UInt256> maxFeeOption = new("--maxfee")
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
            IJsonRpcClient rpcClient = InitRpcClient(
                parseResult.GetValue(rpcUrlOption)!,
                SimpleConsoleLogManager.Instance.GetClassLogger());

            string? chainIdString = await rpcClient.Post<string>("eth_chainId") ?? "1";
            ulong chainId = HexConvert.ToUInt64(chainIdString);

            FundsDistributor distributor = new(rpcClient, chainId, parseResult.GetValue(keyFileOption), SimpleConsoleLogManager.Instance);
            IEnumerable<string> hashes = await distributor.ReclaimFunds(
                new(parseResult.GetValue(receiverOption)!),
                parseResult.GetValue(maxFeeOption),
                parseResult.GetValue(maxPriorityFeeGasOption));
        });
        root.Add(command);
    }

    public static IJsonRpcClient InitRpcClient(string rpcUrl, ILogger logger) =>
        new BasicJsonRpcClient(
            new Uri(rpcUrl),
            new EthereumJsonSerializer(),
            new OneLoggerLogManager(logger)
        );

    public static void SetupSendFileCommand(Command root)
    {
        Command command = new("send")
        {
            Description = "Sends a file"
        };
        Option<string> fileOption = new("--file")
        {
            Description = "File to send as is",
            HelpName = "path",
            Required = true
        };
        Option<string> rpcUrlOption = new("--rpcurl")
        {
            Description = "The URL of the JSON RPC server",
            HelpName = "URL",
            Required = true
        };
        Option<string> privateKeyOption = new("--privatekey")
        {
            Description = "The key to use for sending blobs",
            HelpName = "key",
            Required = true,
        };
        Option<string> receiverOption = new("--receiveraddress")
        {
            Description = "Receiver address of the blobs",
            HelpName = "address",
            Required = true,
        };
        Option<UInt256> maxFeePerBlobGasOption = new("--maxfeeperblobgas")
        {
            DefaultValueFactory = r => 1000,
            Description = "Set the maximum fee per blob data",
            HelpName = "fee"
        };
        Option<ulong> feeMultiplierOption = new("--feemultiplier")
        {
            DefaultValueFactory = r => 1UL,
            Description = "A multiplier to use for gas fees",
            HelpName = "value"
        };
        Option<UInt256?> maxPriorityFeeGasOption = new("--maxpriorityfee")
        {
            Description = "The maximum priority fee for each transaction",
            HelpName = "fee"
        };
        Option<bool> waitOption = new("--wait") { Description = "Wait for tx inclusion" };
        Option<string> forkOption = new("--fork") { Description = "Specify fork for max blob count, target blob count, proof type" };

        command.Add(fileOption);
        command.Add(rpcUrlOption);
        command.Add(privateKeyOption);
        command.Add(receiverOption);
        command.Add(maxFeePerBlobGasOption);
        command.Add(feeMultiplierOption);
        command.Add(maxPriorityFeeGasOption);
        command.Add(waitOption);
        command.Add(forkOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            PrivateKey privateKey = new(parseResult.GetValue(privateKeyOption)!);
            byte[] data = File.ReadAllBytes(parseResult.GetValue(fileOption)!);
            BlobSender sender = new(parseResult.GetValue(rpcUrlOption)!, SimpleConsoleLogManager.Instance);

            string? fork = parseResult.GetValue(forkOption);
            IReleaseSpec spec = fork is null ? Prague.Instance : SpecNameParser.Parse(fork);

            return sender.SendData(
                data,
                privateKey,
                parseResult.GetValue(receiverOption)!,
                parseResult.GetValue(maxFeePerBlobGasOption),
                parseResult.GetValue(feeMultiplierOption),
                parseResult.GetValue(maxPriorityFeeGasOption),
                parseResult.GetValue(waitOption), spec);
        });

        root.Add(command);
    }
}
