// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.CommandLineUtils;
using Nethermind.Cli;
using Nethermind.Cli.Console;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace SendBlobs;
internal static class SetupCli
{
    public static void SetupExecute(CommandLineApplication app)
    {
        app.HelpOption("--help");

        CommandOption rpcUrlOption = app.Option("--rpcurl <rpcUrl>", "Url of the Json RPC.", CommandOptionType.SingleValue);
        CommandOption blobTxOption = app.Option("--bloboptions <blobOptions>", "Options in format '10x1-2', '2x5-5' etc. for the blobs.", CommandOptionType.MultipleValue);
        CommandOption privateKeyOption = app.Option("--privatekey <privateKey>", "The key to use for sending blobs.", CommandOptionType.SingleValue);
        CommandOption privateKeyFileOption = app.Option("--keyfile <keyFile>", "File containing private keys that each blob tx will be send from.", CommandOptionType.SingleValue);
        CommandOption receiverOption = app.Option("--receiveraddress <receiverAddress>", "Receiver address of the blobs.", CommandOptionType.SingleValue);
        CommandOption maxFeePerDataGasOption = app.Option("--maxfeeperdatagas <maxFeePerDataGas>", "(Optional) Set the maximum fee per blob data.", CommandOptionType.SingleValue);
        CommandOption feeMultiplierOption = app.Option("--feemultiplier <feeMultiplier>", "(Optional) A multiplier to use for gas fees.", CommandOptionType.SingleValue);
        CommandOption maxPriorityFeeGasOption = app.Option("--maxpriorityfee <maxPriorityFee>", "(Optional) The maximum priority fee for each transaction.", CommandOptionType.SingleValue);

        app.OnExecute(async () =>
        {
            string rpcUrl = rpcUrlOption.Value();
            (int count, int blobCount, string @break)[] blobTxCounts = ParseTxOptions(blobTxOption.Value());

            PrivateKey[] privateKeys;

            if (privateKeyFileOption.HasValue())
                privateKeys = File.ReadAllLines(privateKeyFileOption.Value()).Select(k => new PrivateKey(k)).ToArray();
            else if (privateKeyOption.HasValue())
                privateKeys = new[] { new PrivateKey(privateKeyOption.Value()) };
            else
            {
                Console.WriteLine("Missing private key argument.");
                app.ShowHelp();
                return 1;
            }

            string receiver = receiverOption.Value();

            UInt256 maxFeePerDataGas = 1000;
            if (maxFeePerDataGasOption.HasValue())
            {
                ulong.TryParse(maxFeePerDataGasOption.Value(), out ulong shortMaxFeePerDataGas);
                maxFeePerDataGas = shortMaxFeePerDataGas;
            }

            ulong feeMultiplier = 4;
            if (feeMultiplierOption.HasValue())
                ulong.TryParse(feeMultiplierOption.Value(), out feeMultiplier);

            UInt256 maxPriorityFeeGasArgs = 0;
            if (maxPriorityFeeGasOption.HasValue()) UInt256.TryParse(maxPriorityFeeGasOption.Value(), out maxPriorityFeeGasArgs);

            ILogger logger = SimpleConsoleLogManager.Instance.GetLogger("send blobs");

            BlobSender sender = new(rpcUrl, logger);
            await sender.Send(
                blobTxCounts,
                privateKeys,
                receiver,
                maxFeePerDataGas,
                feeMultiplier,
                maxPriorityFeeGasArgs);

            return 0;
        });
    }

    private static (int count, int blobCount, string @break)[] ParseTxOptions(string options)
    {
        if (string.IsNullOrWhiteSpace(options))
            return Array.Empty<(int count, int blobCount, string @break)>();

        ReadOnlySpan<char> chars = options.AsSpan();
        var result = new List<(int, int, string)>();

        ReadOnlySpan<char> SplitToNext(ReadOnlySpan<char> line, char separator, bool returnRemainder = false){
            int i = line.IndexOf(separator);
            if (i == -1)
                return returnRemainder ? ReadOnlySpan<char>.Empty : line;    
            return returnRemainder ? line[(i+1)..] : line[..i];
        };

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

    public static void SetupDistributeCommand(CommandLineApplication app)
    {
        app.Command("distribute", (command) =>
        {
            command.Description = "Distribute funds from an address to a number of new addresses.";
            command.HelpOption("--help");

            CommandOption rpcUrlOption = command.Option("--rpcurl <rpcUrl>", "Url of the Json RPC.", CommandOptionType.SingleValue);
            CommandOption privateKeyOption = command.Option("--privatekey <privateKey>", "The private key to distribute funds from.", CommandOptionType.SingleValue);
            CommandOption keyNumberOption = command.Option("--number <number>", "The number of new addresses/keys to make.", CommandOptionType.SingleValue);
            CommandOption keyFileOption = command.Option("--keyfile <keyFile>", "File where the newly generated keys are written.", CommandOptionType.SingleValue);
            CommandOption maxPriorityFeeGasOption = command.Option("--maxpriorityfee <maxPriorityFee>", "(Optional) The maximum priority fee for each transaction.", CommandOptionType.SingleValue);
            CommandOption maxFeeOption = command.Option("--maxfee <maxFee>", "(Optional) The maxFeePerGas fee paid for each transaction.", CommandOptionType.SingleValue);

            command.OnExecute(async () =>
            {
                uint keysToMake = uint.Parse(keyNumberOption.Value());
                PrivateKey privateKey = new(privateKeyOption.Value());

                ILogger logger = SimpleConsoleLogManager.Instance.GetLogger("distribute funds");
                INodeManager nodeManager = InitNodeManager(rpcUrlOption.Value(), logger);

                string? chainIdString = await nodeManager.Post<string>("eth_chainId") ?? "1";
                ulong chainId = HexConvert.ToUInt64(chainIdString);

                Signer signer = new Signer(chainId, privateKey, new OneLoggerLogManager(logger));
                UInt256 maxFee = maxFeeOption.HasValue() ? UInt256.Parse(maxFeeOption.Value()) : 0;
                UInt256 maxPriorityFee = maxPriorityFeeGasOption.HasValue() ? UInt256.Parse(maxPriorityFeeGasOption.Value()) : 0;

                FundsDistributor distributor = new FundsDistributor(nodeManager, chainId, keyFileOption.Value(), new OneLoggerLogManager(logger));
                IEnumerable<string> hashes = await distributor.DitributeFunds(signer, keysToMake, maxFee, maxPriorityFee);

                return 0;
            });
        });
    }

    public static void SetupReclaimCommand(CommandLineApplication app)
    {
        app.Command("reclaim", (command) =>
        {
            command.Description = "Reclaim funds distributed from the 'distribute' command.";
            command.HelpOption("--help");

            CommandOption rpcUrlOption = command.Option("--rpcurl <rpcUrl>", "Url of the Json RPC.", CommandOptionType.SingleValue);
            CommandOption receiverOption = command.Option("--receiveraddress <receiverAddress>", "The address to send the funds to.", CommandOptionType.SingleValue);
            CommandOption keyFileOption = command.Option("--keyfile <keyFile>", "File of the private keys to reclaim from.", CommandOptionType.SingleValue);
            CommandOption maxPriorityFeeGasOption = command.Option("--maxpriorityfee <maxPriorityFee>", "(Optional) The maximum priority fee for each transaction.", CommandOptionType.SingleValue);
            CommandOption maxFeeOption = command.Option("--maxfee <maxFee>", "(Optional) The maxFeePerGas paid for each transaction.", CommandOptionType.SingleValue);

            command.OnExecute(async () =>
            {
                ILogger logger = SimpleConsoleLogManager.Instance.GetLogger("reclaim funds");
                INodeManager nodeManager = InitNodeManager(rpcUrlOption.Value(), logger);

                string? chainIdString = await nodeManager.Post<string>("eth_chainId") ?? "1";
                ulong chainId = HexConvert.ToUInt64(chainIdString);

                Address beneficiary = new Address(receiverOption.Value());

                UInt256 maxFee = maxFeeOption.HasValue() ? UInt256.Parse(maxFeeOption.Value()) : 0;
                UInt256 maxPriorityFee = maxPriorityFeeGasOption.HasValue() ? UInt256.Parse(maxPriorityFeeGasOption.Value()) : 0;

                FundsDistributor distributor = new FundsDistributor(nodeManager, chainId, keyFileOption.Value(), new OneLoggerLogManager(logger));
                IEnumerable<string> hashes = await distributor.ReclaimFunds(beneficiary, maxFee, maxPriorityFee);

                return 0;
            });
        });
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
}
