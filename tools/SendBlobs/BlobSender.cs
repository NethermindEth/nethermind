// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

namespace SendBlobs;
internal class BlobSender
{
    private static readonly TxDecoder txDecoder = new();

    private INodeManager _nodeManager;
    private readonly ILogger _logger;
    private readonly ILogManager _logManager;

    public BlobSender(string rpcUrl, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(rpcUrl);
        ArgumentNullException.ThrowIfNull(logManager);

        _logManager = logManager;
        _logger = logManager.GetClassLogger();
        _nodeManager = SetupCli.InitNodeManager(rpcUrl, _logger);

        KzgPolynomialCommitments.InitializeAsync().Wait();
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
    public async Task SendRandomBlobs(
        (int count, int blobCount, string @break)[] blobTxCounts,
        PrivateKey[] privateKeys,
        string receiver,
        UInt256? maxFeePerBlobGasArgs,
        ulong feeMultiplier,
        UInt256? maxPriorityFeeGasArgs,
        bool waitForInclusion)
    {
        List<(Signer, ulong)> signers = [];

        if (waitForInclusion)
        {
            bool isNodeSynced = await _nodeManager.Post<dynamic>("eth_syncing") is bool;
            if (!isNodeSynced)
            {
                Console.WriteLine($"Will not wait for blob inclusion since selected node at {_nodeManager.CurrentUri} is still syncing");
                waitForInclusion = false;
            }
        }

        string? chainIdString = await _nodeManager.Post<string>("eth_chainId") ?? "1";
        ulong chainId = HexConvert.ToUInt64(chainIdString);

        foreach (PrivateKey privateKey in privateKeys)
        {
            string? nonceString = await _nodeManager.Post<string>("eth_getTransactionCount", privateKey.Address, "latest");
            if (nonceString is null)
            {
                _logger.Error("Unable to get nonce");
                return;
            }
            ulong nonce = HexConvert.ToUInt64(nonceString);

            signers.Add(new(new Signer(chainId, privateKey, _logManager), nonce));
        }

        TxDecoder txDecoder = new();
        Random random = new();

        int signerIndex = -1;

        ulong excessBlobs = (ulong)blobTxCounts.Sum(btxc => btxc.blobCount) / 2;

        foreach ((int txCount, int blobCount, string @break) txs in blobTxCounts)
        {
            int txCount = txs.txCount;
            int blobCount = txs.blobCount;
            string @break = txs.@break;

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
                }

                byte[][] blobs = new byte[blobCount][];

                for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
                {
                    blobs[blobIndex] = new byte[Ckzg.Ckzg.BytesPerBlob];
                    random.NextBytes(blobs[blobIndex]);
                    for (int i = 0; i < Ckzg.Ckzg.BytesPerBlob; i += 32)
                    {
                        blobs[blobIndex][i] = 0;
                    }

                    if (@break == "6" && blobIndex == 0)
                    {
                        Array.Fill(blobs[blobIndex], (byte)0, 0, 31);
                        blobs[blobIndex][31] = 1;
                    }
                }

                (byte[][] blobHashes, ShardBlobNetworkWrapper blobsContainer) = GenerateBlobData(blobs);


                BlockModel<Hash256>? blockResult = await _nodeManager.Post<BlockModel<Hash256>>("eth_getBlockByNumber", "latest", false);

                if (blockResult is null)
                {
                    Console.WriteLine($"Unable to get the latest block, terminating.");
                    return;
                }

                (UInt256 maxGasPrice, UInt256 maxPriorityFeePerGas, UInt256 maxFeePerBlobGas) = await GetGasPrices(null, maxPriorityFeeGasArgs, maxFeePerBlobGasArgs, blockResult, excessBlobs);

                maxPriorityFeePerGas *= feeMultiplier;
                maxGasPrice *= feeMultiplier;
                maxFeePerBlobGas *= feeMultiplier;

                switch (@break)
                {
                    case "3": blobs[0] = blobs[0].Take(blobs.Length - 2).ToArray(); break;
                    case "4": blobsContainer.Proofs[0][2] = (byte)~blobsContainer.Proofs[0][2]; break;
                    case "5": blobsContainer.Commitments[0][2] = (byte)~blobsContainer.Commitments[0][2]; break;
                    case "6":
                        Array.Copy(KzgPolynomialCommitments.BlsModulus.ToBigEndian(), blobs[0], 32);
                        blobs[0][31] += 1;
                        break;
                    case "7": maxFeePerBlobGas = UInt256.MaxValue; break;
                    //case "8": maxFeePerBlobGas = 42_000_000_000; break;
                    case "9": blobsContainer.Proofs = blobsContainer.Proofs.Skip(1).ToArray(); break;
                    case "10": blobsContainer.Commitments = blobsContainer.Commitments.Skip(1).ToArray(); break;
                    case "11": maxFeePerBlobGas = UInt256.MaxValue / Eip4844Constants.GasPerBlob + 1; break;
                }

                Console.WriteLine($"Sending from {signer.Address}. Nonce: {nonce}, GasPrice: {maxGasPrice}, MaxPriorityFeePerGas: {maxPriorityFeePerGas}, MaxFeePerBlobGas {maxFeePerBlobGas}. ");

                Hash256? result = await SendTransaction(chainId, nonce, maxGasPrice, maxPriorityFeePerGas, maxFeePerBlobGas, receiver, blobHashes, blobsContainer, signer);

                if (result is not null)
                    signers[signerIndex] = new(signer, nonce + 1);

                if (waitForInclusion)
                    await WaitForBlobInclusion(_nodeManager, result, blockResult.Number);
            }
        }
    }

    /// <summary>
    /// Send data that fits in one transaction, adds spaces when a byte is out of range.
    /// </summary>
    public async Task SendData(
        byte[] data,
        PrivateKey privateKey,
        string receiver,
        UInt256 maxFeePerBlobGasArgs,
        ulong feeMultiplier,
        UInt256? maxPriorityFeeGasArgs,
        bool waitForInclusion)
    {
        int n = 0;
        data = data
            .Select((s, i) => (i + n) % 32 != 0 ? [s] : (s < 0x73 ? new byte[] { s } : [(byte)(32), s]))
            .SelectMany(b => b).ToArray();

        if (waitForInclusion)
        {
            bool isNodeSynced = await _nodeManager.Post<dynamic>("eth_syncing") is bool;
            if (!isNodeSynced)
            {
                Console.WriteLine($"Will not wait for blob inclusion since selected node at {_nodeManager.CurrentUri} is still syncing");
                waitForInclusion = false;
            }
        }

        string? chainIdString = await _nodeManager.Post<string>("eth_chainId") ?? "1";
        ulong chainId = HexConvert.ToUInt64(chainIdString);


        string? nonceString = await _nodeManager.Post<string>("eth_getTransactionCount", privateKey.Address, "latest");
        if (nonceString is null)
        {
            _logger.Error("Unable to get nonce");
            return;
        }
        ulong nonce = HexConvert.ToUInt64(nonceString);

        Signer signer = new(chainId, privateKey, _logManager);


        int blobCount = (int)Math.Ceiling((decimal)data.Length / Ckzg.Ckzg.BytesPerBlob);

        byte[][] blobs = new byte[blobCount][];

        for (int blobIndex = 0; blobIndex < blobCount; blobIndex++)
        {
            blobs[blobIndex] = new byte[Ckzg.Ckzg.BytesPerBlob];
            Array.Copy(data, blobIndex * Ckzg.Ckzg.BytesPerBlob, blobs[blobIndex], 0, Math.Min(data.Length - blobIndex * Ckzg.Ckzg.BytesPerBlob, Ckzg.Ckzg.BytesPerBlob));
        }

        (byte[][] blobHashes, ShardBlobNetworkWrapper blobsContainer) = GenerateBlobData(blobs);

        BlockModel<Hash256>? blockResult = await _nodeManager.Post<BlockModel<Hash256>>("eth_getBlockByNumber", "latest", false);

        if (blockResult is null)
        {
            Console.WriteLine($"Unable to get the latest block, terminating.");
            return;
        }

        (UInt256 maxGasPrice, UInt256 maxPriorityFeePerGas, UInt256 maxFeePerBlobGas) = await GetGasPrices(null, maxPriorityFeeGasArgs, maxFeePerBlobGasArgs, blockResult!, 1);

        maxPriorityFeePerGas *= feeMultiplier;
        maxGasPrice *= feeMultiplier;
        maxFeePerBlobGas *= feeMultiplier;

        Hash256? hash = await SendTransaction(chainId, nonce, maxGasPrice, maxPriorityFeePerGas, maxFeePerBlobGas, receiver, blobHashes, blobsContainer, signer);

        if (waitForInclusion)
            await WaitForBlobInclusion(_nodeManager, hash, blockResult.Number);
    }

    private async Task<(UInt256 maxGasPrice, UInt256 maxPriorityFeePerGas, UInt256 maxFeePerBlobGas)> GetGasPrices
        (UInt256? defaultGasPrice, UInt256? defaultMaxPriorityFeePerGas, UInt256? defaultMaxFeePerBlobGas, BlockModel<Hash256> block, ulong excessBlobs)
    {
        (UInt256 maxGasPrice, UInt256 maxPriorityFeePerGas, UInt256 maxFeePerBlobGas) result = new();

        if (defaultMaxPriorityFeePerGas is null)
        {
            string? maxPriorityFeePerGasRes = await _nodeManager.Post<string>("eth_maxPriorityFeePerGas") ?? "1";
            result.maxPriorityFeePerGas = HexConvert.ToUInt256(maxPriorityFeePerGasRes);
        }
        else
        {
            result.maxPriorityFeePerGas = defaultMaxPriorityFeePerGas.Value;
        }

        if (defaultGasPrice is null)
        {
            const int minGasPrice = 7;
            result.maxGasPrice = UInt256.Max(minGasPrice, block.BaseFeePerGas) + result.maxPriorityFeePerGas;
        }
        else
        {
            result.maxGasPrice = defaultGasPrice.Value + result.maxPriorityFeePerGas;
        }

        if (defaultMaxFeePerBlobGas is null)
        {
            ulong excessBlobsReserve = 2 * Eip4844Constants.TargetBlobGasPerBlock;
            BlobGasCalculator.TryCalculateBlobGasPricePerUnit(
                (block.ExcessBlobGas ?? 0) +
                excessBlobs * Eip4844Constants.MaxBlobGasPerBlock +
                excessBlobsReserve,
                out UInt256 blobGasPrice);
            result.maxFeePerBlobGas = blobGasPrice;
        }
        else
        {
            result.maxFeePerBlobGas = defaultMaxFeePerBlobGas.Value;
        }

        return result;
    }

    private static (byte[][] hashes, ShardBlobNetworkWrapper blobsContainer) GenerateBlobData(byte[][] blobs)
    {
        byte[][] commitments = new byte[blobs.Length][];
        byte[][] proofs = new byte[blobs.Length][];
        byte[][] blobhashes = new byte[blobs.Length][];

        int blobIndex = 0;
        foreach (var blob in blobs)
        {
            commitments[blobIndex] = new byte[Ckzg.Ckzg.BytesPerCommitment];
            proofs[blobIndex] = new byte[Ckzg.Ckzg.BytesPerProof];
            blobhashes[blobIndex] = new byte[32];

            KzgPolynomialCommitments.KzgifyBlob(
                blobs[blobIndex].AsSpan(),
                commitments[blobIndex].AsSpan(),
                proofs[blobIndex].AsSpan(),
                blobhashes[blobIndex].AsSpan());
            blobIndex++;
        }
        return (blobhashes, new ShardBlobNetworkWrapper(blobs, commitments, proofs));
    }

    private async Task<Hash256?> SendTransaction(ulong chainId, ulong nonce,
        UInt256 gasPrice, UInt256 maxPriorityFeePerGas, UInt256 maxFeePerBlobGas,
        string receiver, byte[][] blobhashes, ShardBlobNetworkWrapper blobsContainer, ISigner signer)
    {
        Transaction tx = new()
        {
            Type = TxType.Blob,
            ChainId = chainId,
            Nonce = nonce,
            GasLimit = GasCostOf.Transaction,
            GasPrice = maxPriorityFeePerGas,
            DecodedMaxFeePerGas = gasPrice,
            MaxFeePerBlobGas = maxFeePerBlobGas,
            Value = 0,
            To = new Address(receiver),
            BlobVersionedHashes = blobhashes,
            NetworkWrapper = blobsContainer,
        };

        await signer.Sign(tx);

        string txRlp = Hex.ToHexString(txDecoder
            .Encode(tx, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping).Bytes);

        string? result = await _nodeManager.Post<string>("eth_sendRawTransaction", "0x" + txRlp);

        Console.WriteLine("Sending tx result:" + result);

        return result is not null ? tx.CalculateHash() : null;
    }

    private async static Task WaitForBlobInclusion(INodeManager nodeManager, Hash256 txHash, UInt256 lastBlockNumber)
    {
        Console.WriteLine("Waiting for blob transaction to be included in a block");
        int waitInMs = 2000;
        //Retry for about 5 slots worth of time
        int retryCount = 12 * 5 * 1000 / waitInMs;

        while (true)
        {
            var blockResult = await nodeManager.Post<BlockModel<Hash256>>("eth_getBlockByNumber", lastBlockNumber.ToString() ?? "latest", false);
            if (blockResult != null)
            {
                lastBlockNumber = blockResult.Number + 1;

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
