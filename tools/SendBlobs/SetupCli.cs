// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Crypto;
using Nethermind.Logging;
using System.CommandLine;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Nethermind.JsonRpc.Client;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Int256;

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
        Option<ulong?> maxPriorityFeeGasOption = new("--maxpriorityfee")
        {
            Description = "The maximum priority fee for each transaction",
            HelpName = "fee"
        };
        Option<bool> waitOption = new("--wait") { Description = "Wait for tx inclusion" };
        Option<string> forkOption = new("--fork") { Description = "Specify fork for MaxBlobCount, TargetBlobCount, Osaka by default" };
        Option<int?> seedOption = new("--seed")
        {
            Description = "Seed for randomness used to generate blob content",
            HelpName = "seed",
            DefaultValueFactory = r => null
        };

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
        command.Add(seedOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            return ExecuteWithRpcHandling(() =>
            {
                PrivateKey[] privateKeys;

                string? privateKeyFileValue = parseResult.GetValue(privateKeyFileOption);
                string? privateKeyValue = parseResult.GetValue(privateKeyOption);

                if (privateKeyFileValue is not null)
                {
                    if (!File.Exists(privateKeyFileValue))
                    {
                        Console.WriteLine($"Private key file not found: {privateKeyFileValue}");
                        return Task.CompletedTask;
                    }

                    privateKeys = File.ReadAllLines(privateKeyFileValue).Select(k => new PrivateKey(k)).ToArray();
                }
                else if (privateKeyValue is not null)
                    privateKeys = [new PrivateKey(privateKeyValue)];
                else
                {
                    Console.WriteLine("Missing private key argument.");
                    return Task.CompletedTask;
                }

                string? fork = parseResult.GetValue(forkOption);
                IReleaseSpec spec = fork is null ? Osaka.Instance : SpecNameParser.Parse(fork);

                BlobSender sender = new(parseResult.GetRequiredValue(rpcUrlOption), SimpleConsoleLogManager.Instance);
                ulong? maxFeePerBlobGas = parseResult.GetValue(maxFeePerBlobGasOption) ??
                                          parseResult.GetValue(maxFeePerDataGasOptionObsolete);
                ulong? maxPriorityFee = parseResult.GetValue(maxPriorityFeeGasOption);

                (int count, int blobCount, string @break)[] txOptions = ParseTxOptions(parseResult.GetValue(blobTxOption));
                if (txOptions.Length == 0)
                {
                    Console.WriteLine("No --bloboptions provided. Nothing to send.");
                    return Task.CompletedTask;
                }

                return sender.SendRandomBlobs(
                    txOptions,
                    privateKeys,
                    parseResult.GetRequiredValue(receiverOption),
                    maxFeePerBlobGas.HasValue ? (UInt256?)maxFeePerBlobGas.Value : null,
                    parseResult.GetValue(feeMultiplierOption),
                    maxPriorityFee.HasValue ? (UInt256?)maxPriorityFee.Value : null,
                    parseResult.GetValue(waitOption),
                    spec,
                    parseResult.GetValue(seedOption));
            });
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
            ReadOnlySpan<char> segment = SplitToNext(chars[offSet..], ',');
            nextComma = segment.Trim();
            if (nextComma.IsEmpty)
            {
                offSet += segment.Length + 1;
                if (offSet > chars.Length)
                {
                    break;
                }
                continue;
            }

            ReadOnlySpan<char> breakSegment = SplitToNext(nextComma, '-', true);
            ReadOnlySpan<char> @break = breakSegment.Trim();
            ReadOnlySpan<char> rest = nextComma[..(nextComma.Length - (breakSegment.Length == 0 ? 0 : breakSegment.Length + 1))];
            ReadOnlySpan<char> count = SplitToNext(rest, 'x').Trim();
            ReadOnlySpan<char> txCount = SplitToNext(rest, 'x', true).Trim();

            result.Add(new(int.Parse(count), txCount.Length == 0 ? 1 : int.Parse(txCount), new string(@break)));

            offSet += segment.Length + 1;
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
            HelpName = "value",
            Required = true
        };
        Option<string> keyFileOption = new("--keyfile")
        {
            Description = "File where the newly generated keys are written",
            HelpName = "path"
        };
        Option<ulong> maxPriorityFeeGasOption = new("--maxpriorityfee")
        {
            Description = "The maximum priority fee for each transaction",
            HelpName = "fee"
        };
        Option<ulong> maxFeeOption = new("--maxfee")
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
        command.SetAction((parseResult, cancellationToken) =>
        {
            return ExecuteWithRpcHandling(async () =>
            {
                IJsonRpcClient rpcClient = InitRpcClient(
                    parseResult.GetRequiredValue(rpcUrlOption),
                    SimpleConsoleLogManager.Instance.GetClassLogger());

                ulong chainId = await rpcClient.GetChainIdAsync();

                Signer signer = new(chainId, new PrivateKey(parseResult.GetRequiredValue(privateKeyOption)),
                    SimpleConsoleLogManager.Instance);

                FundsDistributor distributor = new(
                    rpcClient, chainId, parseResult.GetValue(keyFileOption), SimpleConsoleLogManager.Instance);
                await distributor.DistributeFunds(
                    signer,
                    parseResult.GetValue(keyNumberOption),
                    parseResult.GetValue(maxFeeOption),
                    parseResult.GetValue(maxPriorityFeeGasOption));
            });
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
            HelpName = "path",
            Required = true
        };
        Option<ulong> maxPriorityFeeGasOption = new("--maxpriorityfee")
        {
            Description = "The maximum priority fee for each transaction",
            HelpName = "fee"
        };
        Option<ulong> maxFeeOption = new("--maxfee")
        {
            Description = "The maxFeePerGas fee paid for each transaction",
            HelpName = "fee"
        };

        command.Add(rpcUrlOption);
        command.Add(receiverOption);
        command.Add(keyFileOption);
        command.Add(maxPriorityFeeGasOption);
        command.Add(maxFeeOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            return ExecuteWithRpcHandling(async () =>
            {
                string keyFilePath = parseResult.GetRequiredValue(keyFileOption);
                if (!File.Exists(keyFilePath))
                {
                    Console.WriteLine($"Key file not found: {keyFilePath}");
                    return;
                }

                IJsonRpcClient rpcClient = InitRpcClient(
                    parseResult.GetRequiredValue(rpcUrlOption),
                    SimpleConsoleLogManager.Instance.GetClassLogger());

                ulong chainId = await rpcClient.GetChainIdAsync();

                FundsDistributor distributor = new(rpcClient, chainId, parseResult.GetValue(keyFileOption), SimpleConsoleLogManager.Instance);
                await distributor.ReclaimFunds(
                    new(parseResult.GetRequiredValue(receiverOption)),
                    parseResult.GetValue(maxFeeOption),
                    parseResult.GetValue(maxPriorityFeeGasOption));
            });
        });
        root.Add(command);
    }

    private static async Task ExecuteWithRpcHandling(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (RpcException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.Exit(-1);
        }
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
        Option<ulong> maxFeePerBlobGasOption = new("--maxfeeperblobgas")
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
        Option<ulong?> maxPriorityFeeGasOption = new("--maxpriorityfee")
        {
            Description = "The maximum priority fee for each transaction",
            HelpName = "fee"
        };
        Option<bool> waitOption = new("--wait") { Description = "Wait for tx inclusion" };
        Option<string> forkOption = new("--fork") { Description = "Specify fork for max blob count, target blob count, proof type. Osaka by default" };

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
            return ExecuteWithRpcHandling(() =>
            {
                PrivateKey privateKey = new(parseResult.GetRequiredValue(privateKeyOption));
                string filePath = parseResult.GetRequiredValue(fileOption);
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"File not found: {filePath}");
                    return Task.CompletedTask;
                }

                byte[] data = File.ReadAllBytes(filePath);
                BlobSender sender = new(parseResult.GetRequiredValue(rpcUrlOption), SimpleConsoleLogManager.Instance);

                string? fork = parseResult.GetValue(forkOption);
                IReleaseSpec spec = fork is null ? Osaka.Instance : SpecNameParser.Parse(fork);

                return sender.SendData(
                    data,
                    privateKey,
                    parseResult.GetRequiredValue(receiverOption),
                    (UInt256)parseResult.GetValue(maxFeePerBlobGasOption),
                    parseResult.GetValue(feeMultiplierOption),
                    (UInt256?)parseResult.GetValue(maxPriorityFeeGasOption),
                    parseResult.GetValue(waitOption), spec);
            });
        });

        root.Add(command);
    }
}
