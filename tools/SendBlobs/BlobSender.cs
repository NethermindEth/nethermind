// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Cli;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Org.BouncyCastle.Utilities.Encoders;
using Nethermind.Cli.Console;
using Nethermind.Serialization.Json;

namespace SendBlobs;
internal class BlobSender
{
    private INodeManager _nodeManager;
    private readonly ILogger _logger;
    public BlobSender(string rpcUrl, ILogger logger)
    {
        if (rpcUrl is null) throw new ArgumentNullException(nameof(rpcUrl));
        if (logger is null) throw new ArgumentNullException(nameof(logger));
        _nodeManager = SetupCli.InitNodeManager(rpcUrl, logger);
        _logger = logger;
    }

    // send-blobs <url-without-auth> <transactions-send-formula 10x1,4x2,3x6> <secret-key> <receiver-address>
    // send-blobs http://localhost:8545 5 0x0000000000000000000000000000000000000000000000000000000000000000 0x000000000000000000000000000000000000f1c1 100 100
    // 1 = 0 blobs
    // 2 = 1st blob is of wrong size
    // 3 = 7 blobs
    // 4 = 1st blob's wrong proof
    // 5 = 1st blob's wrong commitment
    // 6 = 1st blob with a modulo correct, but > modulo value
    // 7 = max fee per blob gas = max value
    // 9 = 1st proof removed
    // 10 = 1st commitment removed
    // 11 = max fee per blob gas = max value / blobgasperblob + 1
    // 14 = 100 blobs
    // 15 = 1000 blobs
    public async Task Send(
        (int count, int blobCount, string @break)[] blobTxCounts,
        PrivateKey[] privateKeys,
        string receiver,
        UInt256 maxFeePerDataGas,
        ulong feeMultiplier,
        UInt256 maxPriorityFeeGasArgs)
    {
        await KzgPolynomialCommitments.InitializeAsync();

        List<(Signer, ulong)> signers = new List<(Signer, ulong)>();

        bool isNodeSynced = await _nodeManager.Post<dynamic>("eth_syncing") is bool;

        string? chainIdString = await _nodeManager.Post<string>("eth_chainId") ?? "1";
        ulong chainId = HexConvert.ToUInt64(chainIdString);

        OneLoggerLogManager logManager = new(_logger);

        foreach (PrivateKey privateKey in privateKeys)
        {
            string? nonceString = await _nodeManager.Post<string>("eth_getTransactionCount", privateKey.Address, "latest");
            if (nonceString is null)
            {
                _logger.Error("Unable to get nonce");
                return;
            }
            ulong nonce = HexConvert.ToUInt64(nonceString);

            signers.Add(new(new Signer(chainId, privateKey, logManager), nonce));
        }

        TxDecoder txDecoder = new();
        int signerIndex = -1;

        foreach ((int txCount, int blobCount, string @break) txs in blobTxCounts)
        {
            int txCount = txs.txCount;
            int blobCount = txs.blobCount;
            string @break = txs.@break;
            bool waitForBlock = false;

            while (txCount > 0)
            {
                txCount--;

                signerIndex++;
                if (signerIndex >= signers.Count)
                    signerIndex = 0;

                Signer signer = signers[signerIndex].Item1;
                ulong nonce = signers[signerIndex].Item2;

                switch (@break)
                {
                    case "1": blobCount = 0; break;
                    case "2": blobCount = 7; break;
                    case "14": blobCount = 100; break;
                    case "15": blobCount = 1000; break;
                    case "wait":
                        waitForBlock = isNodeSynced;
                        if (!isNodeSynced) Console.WriteLine($"Will not wait for blob inclusion since selected node at {_nodeManager.CurrentUri} is still syncing");
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

                string? gasPriceRes = await _nodeManager.Post<string>("eth_gasPrice") ?? "1";
                UInt256 gasPrice = HexConvert.ToUInt256(gasPriceRes);

                string? maxPriorityFeePerGasRes = await _nodeManager.Post<string>("eth_maxPriorityFeePerGas") ?? "1";
                UInt256 maxPriorityFeePerGas = HexConvert.ToUInt256(maxPriorityFeePerGasRes);

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
                    //case "8": maxFeePerDataGas = 42_000_000_000; break;
                    case "9": proofs = proofs.Skip(1).ToArray(); break;
                    case "10": commitments = commitments.Skip(1).ToArray(); break;
                    case "11": maxFeePerDataGas = UInt256.MaxValue / Eip4844Constants.BlobGasPerBlob + 1; break;
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
                    MaxFeePerBlobGas = maxFeePerDataGas,
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
                    blockResult = await _nodeManager.Post<BlockModel<Keccak>>("eth_getBlockByNumber", "latest", false);

                string? result = await _nodeManager.Post<string>("eth_sendRawTransaction", "0x" + txRlp);

                Console.WriteLine("Result:" + result);

                if (result != null)
                    signers[signerIndex] = new(signer, nonce + 1);

                if (blockResult != null && waitForBlock)
                    await WaitForBlobInclusion(_nodeManager, tx.CalculateHash(), blockResult.Number);
            }
        }
    }

    private async static Task WaitForBlobInclusion(INodeManager nodeManager, Keccak txHash, UInt256 lastBlockNumber)
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

}
