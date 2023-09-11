// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Nethermind.Cli;
using Nethermind.Cli.Console;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.WebSockets;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Sockets;
using Org.BouncyCastle.Utilities.Encoders;

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


if (args.Length < 4)
{
    Console.WriteLine("Try:\n\n  send-blobs <url-without-auth> <transactions-send-formula 10x1,4x2,3x6> <secret-key> <receiver-address>\n");
    return;
}

string rpcUrl = args[0];
(int count, int blobCount, string @break)[] blobTxCounts = args[1].Split(',')
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
string privateKeyString = args[2];
string receiver = args[3];

UInt256 maxFeePerDataGas = 1000;
if (args.Length > 4)
{
    ulong.TryParse(args[4], out ulong shortMaxFeePerDataGas);
    maxFeePerDataGas = shortMaxFeePerDataGas;
}

ulong feeMultiplier = 4;
if (args.Length > 5) ulong.TryParse(args[5], out feeMultiplier);


bool waitForBlobInclusion = false;
if (args.Length > 6) bool.TryParse(args[6], out waitForBlobInclusion);

await KzgPolynomialCommitments.InitializeAsync();

PrivateKey privateKey = new(privateKeyString);

ILogger logger = SimpleConsoleLogManager.Instance.GetLogger("send blobs");
ICliConsole cliConsole = new CliConsole();
IJsonSerializer serializer = new EthereumJsonSerializer();
ILogManager logManager = new OneLoggerLogManager(logger);
ICliEngine engine = new CliEngine(cliConsole);
INodeManager nodeManager = new NodeManager(engine, serializer, cliConsole, logManager);
nodeManager.SwitchUri(new Uri(rpcUrl));

string? nonceString = await nodeManager.Post<string>("eth_getTransactionCount", privateKey.Address, "latest");
if (nonceString is null)
{
    logger.Error("Unable to get nonce");
    return;
}

if (waitForBlobInclusion)
{
    var syncResult = await nodeManager.Post<dynamic>("eth_syncing");

    if (syncResult is not bool)
    {
        waitForBlobInclusion = false;
        logger.Info($"Will not wait for blob inclusion since selected node at {rpcUrl} is still syncing");
    }
}


string? chainIdString = await nodeManager.Post<string>("eth_chainId") ?? "1";
ulong chainId = Convert.ToUInt64(chainIdString, chainIdString.StartsWith("0x") ? 16 : 10);

Signer signer = new Signer(chainId, privateKey, new OneLoggerLogManager(logger));
TxDecoder txDecoder = new();

ulong nonce = Convert.ToUInt64(nonceString, nonceString.StartsWith("0x") ? 16 : 10);

foreach ((int txCount, int blobCount, string @break) txs in blobTxCounts)
{
    int txCount = txs.txCount;
    int blobCount = txs.blobCount;
    string @break = txs.@break;

    while (txCount > 0)
    {
        txCount--;
        switch (@break)
        {
            case "1": blobCount = 0; break;
            case "2": blobCount = 7; break;
            case "14": blobCount = 100; break;
            case "15": blobCount = 1000; break;
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

        Console.WriteLine($"Nonce: {nonce}, GasPrice: {gasPrice}, MaxPriorityFeePerGas: {maxPriorityFeePerGas}, WaitForBlobInclusion: {waitForBlobInclusion}");

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

        Transaction tx = new()
        {
            Type = TxType.Blob,
            ChainId = chainId,
            Nonce = nonce,
            GasLimit = GasCostOf.Transaction,
            GasPrice = gasPrice * feeMultiplier,
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


        var blockResult = await nodeManager.Post<BlockModel<Keccak>>("eth_getBlockByNumber", "latest", false);
       
        string? result = await nodeManager.Post<string>("eth_sendRawTransaction", "0x" + txRlp);

        Console.WriteLine("Result:" + result);
        nonce++;

        if (txCount > 0 && blockResult != null && waitForBlobInclusion) 
            await WaitForBlobInclusion(nodeManager, tx.CalculateHash(), blockResult.Number, logger);    
    }
}

async Task WaitForBlobInclusion(INodeManager nodeManager, Keccak txHash, UInt256 lastBlockNumber, ILogger logger)
{
    logger.Info("Waiting for blob transaction to be included in a block");

    while (true)
    {
        var blockResult = await nodeManager.Post<BlockModel<Keccak>>("eth_getBlockByNumber", lastBlockNumber, false);
        if (blockResult != null)
        {
            lastBlockNumber++;
            
            if (blockResult.Transactions.Contains(txHash))
            {
                logger.Info($"Found blob transaction in block {blockResult.Number}");
                return;
            }
        }
        else
        {
            await Task.Delay(2000);
        }
    }
}
