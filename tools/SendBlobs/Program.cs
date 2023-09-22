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

// send-blobs <url-without-auth> <transactions-send-formula 10x1,4x2,3x6> <secret-key> <receiver-address>
// send-blobs http://localhost:8545 5 0x0000000000000000000000000000000000000000000000000000000000000000 0x000000000000000000000000000000000000f1c1 100 100
// 1 = 0 blobs
// 2 = 1st blob is of wrong size
// 3 = 7 blobs
// 4 = 1st blob's wrong proof
// 5 = 1st blob's wrong commitment
// 6 = 1st blob with a modulo correct, but > modulo value
// 7 = max fee per blob gas = max value
// 8 = max fee per blob gas > max value
// 9 = 1st proof removed
// 10 = 1st commitment removed
// 11 = max fee per blob gas = max value / blobgasperblob + 1
// 14 = 100 blobs
// 15 = 1000 blobs

CommandLineApplication app = new() { Name = "SendBlobs" };

SetupExecute(app);
SetupDistributeCommand(app);
SetupReclaimCommand(app);

try
{
    app.Execute(args);
}
catch (CommandParsingException ex)
{
    Console.WriteLine(ex.Message);
    app.ShowHelp();
}

static void SetupExecute(CommandLineApplication app)
{
    app.HelpOption("--help");

    CommandOption rpcUrlOption = app.Option("--rpcurl <rpcUrl>", "Url of the Json RPC.", CommandOptionType.SingleValue);
    CommandOption blobTxOption = app.Option("--bloboptions <blobOptions>", "Options in format '10x1-2', '2x5-5' etc. for the blobs.", CommandOptionType.MultipleValue);
    CommandOption privateKeyOption = app.Option("--privatekey <privateKey>", "The key to use for sending blobs.", CommandOptionType.SingleValue);
    CommandOption privateKeyFileOption = app.Option("--privatekeyfile <privateKeyFile>", "File containing private keys that each blob tx will be send from.", CommandOptionType.SingleValue);
    CommandOption receiverOption = app.Option("--receiveraddress <receiverAddress>", "Receiver address of the blobs.", CommandOptionType.SingleValue);
    CommandOption maxFeePerDataGasOption = app.Option("--maxfeeperdatagas <maxFeePerDataGas>", "(Optional) Set the maximum fee per blob data.", CommandOptionType.SingleValue);
    CommandOption feeMultiplierOption = app.Option("--feemultiplier <feeMultiplier>", "(Optional) A multiplier to use for gas fees.", CommandOptionType.SingleValue);
    CommandOption maxPriorityFeeGasOption = app.Option("--maxpriorityfee <maxPriorityFee>", "(Optional) The maximum priority fee for each transaction.", CommandOptionType.SingleValue);

    app.OnExecute(async () =>
    {
        string rpcUrl = rpcUrlOption.Value();
        (int count, int blobCount, string @break)[] blobTxCounts = blobTxOption.Value().Split(',')
            .Select(x =>
            {
                string @break = "";
                if (x.Contains("-"))
                {
                    @break = x.Split("-")[1];
                    x = x.Split("-")[0];
                }
                return x.Contains("x") ?
                   (int.Parse(x.Split('x')[0]), int.Parse(x.Split('x')[1]), @break)
                 : (int.Parse(x), 1, @break);
            })
            .ToArray();

        //PrivateKey[] privateKeys;
        //if (privateKeyOption.HasValue())
        //    privateKeys = new[] { new PrivateKey(privateKeyOption.Value()) };
        //else if (privateKeyFileOption.HasValue())
        //    privateKeys = File.ReadAllLines(privateKeyFileOption.Value(), System.Text.Encoding.ASCII).Select(k=> new PrivateKey(k)).ToArray();

        string privateKeyString = privateKeyOption.Value();

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

        await SendBlobs(
            rpcUrl,
            blobTxCounts,
            privateKeyString,
            receiver,
            maxFeePerDataGas,
            feeMultiplier,
            maxPriorityFeeGasArgs);
        
        return 0;
    });
}

async static Task SendBlobs(
    string rpcUrl,
    (int count, int blobCount, string @break)[] blobTxCounts,
    string privateKeyString,
    string receiver,
    UInt256 maxFeePerDataGas,
    ulong feeMultiplier,
    UInt256 maxPriorityFeeGasArgs)
{
    await KzgPolynomialCommitments.InitializeAsync();

    PrivateKey privateKey = new(privateKeyString);
    //PrivateKey[] privateKeys = Array.Empty<PrivateKey>();

    ILogger logger = SimpleConsoleLogManager.Instance.GetLogger("send blobs");
    INodeManager nodeManager = InitNodeManager(rpcUrl, logger);

    string? nonceString = await nodeManager.Post<string>("eth_getTransactionCount", privateKey.Address, "latest");
    if (nonceString is null)
    {
        logger.Error("Unable to get nonce");
        return;
    }

    bool isNodeSynced = await nodeManager.Post<dynamic>("eth_syncing") is bool;

    string? chainIdString = await nodeManager.Post<string>("eth_chainId") ?? "1";
    ulong chainId = Convert.ToUInt64(chainIdString, chainIdString.StartsWith("0x") ? 16 : 10);

    Signer signer = new (chainId, privateKey, new OneLoggerLogManager(logger));

    TxDecoder txDecoder = new();

    ulong nonce = Convert.ToUInt64(nonceString, nonceString.StartsWith("0x") ? 16 : 10);

    foreach ((int txCount, int blobCount, string @break) txs in blobTxCounts)
    {
        int txCount = txs.txCount;
        int blobCount = txs.blobCount;
        string @break = txs.@break;
        bool waitForBlock = false;

        while (txCount > 0)
        {
            txCount--;
            switch (@break)
            {
                case "1": blobCount = 0; break;
                case "2": blobCount = 7; break;
                case "14": blobCount = 100; break;
                case "15": blobCount = 1000; break;
                case "wait":
                    waitForBlock = isNodeSynced;
                    if (!isNodeSynced) Console.WriteLine($"Will not wait for blob inclusion since selected node at {rpcUrl} is still syncing");
                    break;
            }

            byte[][] blobs = new byte[blobCount][];
            byte[][] commitments = new byte[blobCount][];
            byte[][] proofs = new byte[blobCount][];
            byte[][] blobhashes = new byte[blobCount][];

            for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
            {
                blobs[blobIndex] = new byte[Ckzg.Ckzg.BytesPerBlob];
                new Random().NextBytes(blobs[blobIndex]);
                for (int i = 0; i < Ckzg.Ckzg.BytesPerBlob; i += 32)
                {
                    blobs[blobIndex][i] = 0;
                }

                if (@break == "6" && blobIndex == 0)
                {
                    Array.Fill(blobs[blobIndex], (byte)0, 0, 31);
                    blobs[blobIndex][31] = 1;
                }

                commitments[blobIndex] = new byte[Ckzg.Ckzg.BytesPerCommitment];
                proofs[blobIndex] = new byte[Ckzg.Ckzg.BytesPerProof];
                blobhashes[blobIndex] = new byte[32];

                KzgPolynomialCommitments.KzgifyBlob(
                    blobs[blobIndex].AsSpan(),
                    commitments[blobIndex].AsSpan(),
                    proofs[blobIndex].AsSpan(),
                    blobhashes[blobIndex].AsSpan());
            }

            string? gasPriceRes = await nodeManager.Post<string>("eth_gasPrice") ?? "1";
            UInt256 gasPrice = (UInt256)Convert.ToUInt64(gasPriceRes, gasPriceRes.StartsWith("0x") ? 16 : 10);

            string? maxPriorityFeePerGasRes = await nodeManager.Post<string>("eth_maxPriorityFeePerGas") ?? "1";
            UInt256 maxPriorityFeePerGas = (UInt256)Convert.ToUInt64(maxPriorityFeePerGasRes, maxPriorityFeePerGasRes.StartsWith("0x") ? 16 : 10);

            Console.WriteLine($"Nonce: {nonce}, GasPrice: {gasPrice}, MaxPriorityFeePerGas: {maxPriorityFeePerGas}");

            switch (@break)
            {
                case "3": blobs[0] = blobs[0].Take(blobs.Length - 2).ToArray(); break;
                case "4": proofs[0][2] = (byte)~proofs[0][2]; break;
                case "5": commitments[0][2] = (byte)~commitments[0][2]; break;
                case "6":
                    Array.Copy(KzgPolynomialCommitments.BlsModulus.ToBigEndian(), blobs[0], 32);
                    blobs[0][31] += 1;
                    break;
                case "7": maxFeePerDataGas = UInt256.MaxValue; break;
                case "8": maxFeePerDataGas = 42_000_000_000; break;
                case "9": proofs = proofs.Skip(1).ToArray(); break;
                case "10": commitments = commitments.Skip(1).ToArray(); break;
                case "11": maxFeePerDataGas = UInt256.MaxValue / Eip4844Constants.DataGasPerBlob + 1; break;
            }

            UInt256 adjustedMaxPriorityFeePerGas = maxPriorityFeeGasArgs == 0 ? maxPriorityFeePerGas : maxPriorityFeeGasArgs;
            Transaction tx = new()
            {
                Type = TxType.Blob,
                ChainId = chainId,
                Nonce = nonce,
                GasLimit = GasCostOf.Transaction,
                GasPrice = adjustedMaxPriorityFeePerGas * feeMultiplier,
                DecodedMaxFeePerGas = gasPrice * feeMultiplier,
                MaxFeePerDataGas = maxFeePerDataGas,
                Value = 0,
                To = new Address(receiver),
                BlobVersionedHashes = blobhashes,
                NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs),
            };

            await signer.Sign(tx);

            string txRlp = Hex.ToHexString(txDecoder
                .Encode(tx, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping).Bytes);

            BlockModel<Keccak>? blockResult = null;
            if (waitForBlock)
                blockResult = await nodeManager.Post<BlockModel<Keccak>>("eth_getBlockByNumber", "latest", false);

            string? result = await nodeManager.Post<string>("eth_sendRawTransaction", "0x" + txRlp);

            Console.WriteLine("Result:" + result);
            nonce++;

            if (blockResult != null && waitForBlock)
                await WaitForBlobInclusion(nodeManager, tx.CalculateHash(), blockResult.Number);
        }
    }
}

async static Task WaitForBlobInclusion(INodeManager nodeManager, Keccak txHash, UInt256 lastBlockNumber)
{
    Console.WriteLine("Waiting for blob transaction to be included in a block");
    int waitInMs = 2000;
    //Retry for about 5 slots worth of time
    int retryCount = (12 * 5 * 1000) / waitInMs;
    while (true)
    {
        var blockResult = await nodeManager.Post<BlockModel<Keccak>>("eth_getBlockByNumber", lastBlockNumber, false);
        if (blockResult != null)
        {
            lastBlockNumber++;

            if (blockResult.Transactions.Contains(txHash))
            {
                string? receipt = await nodeManager.Post<string>("eth_getTransactionByHash", txHash.ToString(), true);

                Console.WriteLine($"Found blob transaction in block {blockResult.Number}");
                return;
            }
        }
        else
        {
            await Task.Delay(waitInMs);
        }

        retryCount--;
        if (retryCount == 0) break;
    }
}

static void SetupDistributeCommand(CommandLineApplication app)
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
            ulong chainId = Convert.ToUInt64(chainIdString, chainIdString.StartsWith("0x") ? 16 : 10);

            Signer signer = new Signer(chainId, privateKey, new OneLoggerLogManager(logger));
            UInt256 maxFee = maxFeeOption.HasValue() ? UInt256.Parse(maxFeeOption.Value()) : 0;
            UInt256 maxPriorityFee = maxPriorityFeeGasOption.HasValue() ? UInt256.Parse(maxPriorityFeeGasOption.Value()) : 0;

            IEnumerable<string> hashes = await FundsDistributor.DitributeFunds(nodeManager, chainId, signer, keysToMake, keyFileOption.Value(), maxFee, maxPriorityFee);

            return 0;
        });

    });
}

static void SetupReclaimCommand(CommandLineApplication app)
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
            ulong chainId = Convert.ToUInt64(chainIdString, chainIdString.StartsWith("0x") ? 16 : 10);

            Address beneficiary = new Address(receiverOption.Value());

            UInt256 maxFee = maxFeeOption.HasValue() ? UInt256.Parse(maxFeeOption.Value()) : 0;
            UInt256 maxPriorityFee = maxPriorityFeeGasOption.HasValue() ? UInt256.Parse(maxPriorityFeeGasOption.Value()) : 0;

            IEnumerable<string> hashes = await FundsDistributor.ReclaimFunds(nodeManager, chainId, beneficiary, keyFileOption.Value(), new OneLoggerLogManager(logger), maxFee, maxPriorityFee);

            return 0;            
        });

    });
}

static INodeManager InitNodeManager(string rpcUrl, ILogger logger)
{
    ICliConsole cliConsole = new CliConsole();
    IJsonSerializer serializer = new EthereumJsonSerializer();
    OneLoggerLogManager logManager = new OneLoggerLogManager(logger);
    ICliEngine engine = new CliEngine(cliConsole);
    INodeManager nodeManager = new NodeManager(engine, serializer, cliConsole, logManager);
    nodeManager.SwitchUri(new Uri(rpcUrl));
    return nodeManager;
}
