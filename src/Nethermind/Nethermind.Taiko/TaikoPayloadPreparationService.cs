// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Taiko;

public class TaikoPayloadPreparationService(
    IBlockchainProcessor processor,
    IWorldState worldState,
    IL1OriginStore l1OriginStore,
    ILogManager logManager,
    IRlpStreamDecoder<Transaction> txDecoder) : IPayloadPreparationService
{
    private const int _emptyBlockProcessingTimeout = 2000;
    private readonly SemaphoreSlim _worldStateLock = new(1);

    private readonly ILogger _logger = logManager.GetClassLogger();

    private readonly ConcurrentDictionary<string, IBlockProductionContext> _payloadStorage = new();

    public string? StartPreparingPayload(BlockHeader parentHeader, PayloadAttributes payloadAttributes)
    {
        TaikoPayloadAttributes attrs = (payloadAttributes as TaikoPayloadAttributes)
            ?? throw new InvalidOperationException("Payload attributes have incorrect type. Expected TaikoPayloadAttributes.");

        string payloadId = payloadAttributes.GetPayloadId(parentHeader);

        _payloadStorage.AddOrUpdate(payloadId, (payloadId) =>
            {
                Block block = BuildBlock(parentHeader, attrs);
                Hash256 parentStateRoot = parentHeader.StateRoot ?? throw new InvalidOperationException("Parent state root is null");
                block = ProcessBlock(block, parentStateRoot);

                // L1Origin **MUST NOT** be null, it's a required field in PayloadAttributes.
                L1Origin l1Origin = attrs.L1Origin ?? throw new InvalidOperationException("L1Origin is required");

                // Set the block hash before inserting the L1Origin into database.
                l1Origin.L2BlockHash = block.Hash;

                // Write L1Origin.
                l1OriginStore.WriteL1Origin(l1Origin.BlockId, l1Origin);
                // Write the head L1Origin.
                l1OriginStore.WriteHeadL1Origin(l1Origin.BlockId);

                // ignore TryAdd failure (it can only happen if payloadId is already in the dictionary)
                return new NoBlockProductionContext(block, UInt256.Zero);
            },
            (payloadId, existing) =>
            {
                if (_logger.IsInfo) _logger.Info($"Payload with the same parameters has already started. PayloadId: {payloadId}");
                return existing;
            });

        return payloadId;
    }

    private Block ProcessBlock(Block block, Hash256 parentStateRoot, CancellationToken token = default)
    {
        if (_worldStateLock.Wait(_emptyBlockProcessingTimeout))
        {
            try
            {
                if (worldState.HasStateForRoot(parentStateRoot))
                {
                    worldState.StateRoot = parentStateRoot;

                    return processor.Process(block, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance, token)
                        ?? throw new InvalidOperationException("Block processing failed");
                }
            }
            finally
            {
                _worldStateLock.Release();
            }
        }

        throw new EmptyBlockProductionException("Setting state for processing block failed");
    }

    private static BlockHeader BuildHeader(BlockHeader parentHeader, TaikoPayloadAttributes payloadAttributes)
    {
        BlockHeader header = new(
            parentHeader.Hash!,
            Keccak.OfAnEmptySequenceRlp,
            payloadAttributes.BlockMetadata!.Beneficiary!,
            UInt256.Zero,
            parentHeader.Number + 1,
            payloadAttributes.BlockMetadata.GasLimit,
            payloadAttributes.Timestamp,
            payloadAttributes.BlockMetadata.ExtraData!)
        {
            MixHash = payloadAttributes.BlockMetadata.MixHash,
            ParentBeaconBlockRoot = payloadAttributes.ParentBeaconBlockRoot,
            BaseFeePerGas = payloadAttributes.BaseFeePerGas,
            Difficulty = UInt256.Zero,
            TotalDifficulty = UInt256.Zero
        };

        return header;
    }

    private Transaction[] BuildTransactions(TaikoPayloadAttributes payloadAttributes)
    {
        RlpStream rlpStream = new(payloadAttributes.BlockMetadata!.TxList!);

        int transactionsSequenceLength = rlpStream.ReadSequenceLength();
        int transactionsCheck = rlpStream.Position + transactionsSequenceLength;

        int txCount = rlpStream.PeekNumberOfItemsRemaining(transactionsCheck);

        Transaction[] transactions = new Transaction[txCount];
        int txIndex = 0;

        while (rlpStream.Position < transactionsCheck)
        {
            transactions[txIndex++] = txDecoder.Decode(rlpStream, RlpBehaviors.None)!;
        }

        rlpStream.Check(transactionsCheck);

        return transactions;
    }

    private Block BuildBlock(BlockHeader parentHeader, TaikoPayloadAttributes payloadAttributes)
    {
        BlockHeader header = BuildHeader(parentHeader, payloadAttributes);
        Transaction[] transactions = BuildTransactions(payloadAttributes);

        return new BlockToProduce(header, transactions, [], payloadAttributes.Withdrawals);
    }

    public ValueTask<IBlockProductionContext?> GetPayload(string payloadId, bool skipCancel = false)
    {
        if (_payloadStorage.TryRemove(payloadId, out IBlockProductionContext? blockContext))
            return ValueTask.FromResult<IBlockProductionContext?>(blockContext);

        return ValueTask.FromResult<IBlockProductionContext?>(null);
    }


    public event EventHandler<BlockEventArgs>? BlockImproved { add { } remove { } }
}
