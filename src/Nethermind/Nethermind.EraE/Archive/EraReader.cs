// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.EraE.E2Store;
using AccumulatorCalculator = Nethermind.Era1.AccumulatorCalculator;
using EraException = Nethermind.Era1.EraException;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;
using Nethermind.EraE.Proofs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.EraE.Archive;

public sealed class EraReader(E2StoreReader e2) : IAsyncEnumerable<(Block, TxReceipt[])>, IDisposable
{
    private readonly EraSlimReceiptDecoder _slimReceiptDecoder = new();
    private readonly ReceiptMessageDecoder _fullReceiptDecoder = new();
    private readonly BlockBodyDecoder _blockBodyDecoder = BlockBodyDecoder.Instance;
    private readonly HeaderDecoder _headerDecoder = new();

    public long FirstBlock => e2.First;
    public long LastBlock => e2.LastBlock;

    public EraReader(string fileName) : this(new E2StoreReader(fileName)) { }

    public async IAsyncEnumerator<(Block, TxReceipt[])> GetAsyncEnumerator(CancellationToken cancellation = default)
    {
        for (long blockNumber = e2.First; blockNumber <= e2.LastBlock; blockNumber++)
        {
            (Block block, TxReceipt[] receipts) = await ReadBlockAndReceipts(blockNumber, cancellation);
            yield return (block, receipts);
        }
    }

    public async Task<(Block, TxReceipt[])> GetBlockByNumber(long number, CancellationToken cancellation = default)
    {
        if (number < e2.First)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be less than first block {e2.First}.");
        if (number > e2.LastBlock)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot exceed last block {e2.LastBlock}.");

        return await ReadBlockAndReceipts(number, cancellation);
    }

    public ValueHash256 ReadAccumulatorRoot()
    {
        long offset = e2.AccumulatorRootOffset;
        if (offset < 0)
            throw new EraException("This EraE file does not contain an AccumulatorRoot (post-merge epoch).");

        e2.ReadEntryAndDecode(
            offset,
            static buffer => new ValueHash256(buffer.Span),
            EntryTypes.AccumulatorRoot,
            out ValueHash256 root);

        return root;
    }

    public async Task<ValueHash256> VerifyContent(
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        int verifyConcurrency = 0,
        Validator? validator = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(specProvider);
        ArgumentNullException.ThrowIfNull(blockValidator);

        if (verifyConcurrency <= 0)
            verifyConcurrency = Environment.ProcessorCount;

        long startBlock = e2.First;
        int blockCount = (int)e2.BlockCount;

        (Hash256 Hash, UInt256 Td, bool IsPreMerge)[] blockMeta =
            new (Hash256 Hash, UInt256 Td, bool IsPreMerge)[blockCount];

        ConcurrentQueue<long> blockNumbers = new();
        for (long n = startBlock; n <= e2.LastBlock; n++)
        {
            blockNumbers.Enqueue(n);
        }

        Task[] workers = new Task[verifyConcurrency];
        for (int i = 0; i < verifyConcurrency; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                while (blockNumbers.TryDequeue(out long blockNumber))
                {
                    (Block block, TxReceipt[] receipts) = await ReadBlockAndReceipts(blockNumber, cancellation);

                    if (!blockValidator.ValidateBodyAgainstHeader(block.Header, block.Body, out string? error))
                        throw new EraVerificationException($"Mismatched block body against header: {error}. Block {blockNumber}.");

                    Hash256 receiptRoot = ReceiptTrie.CalculateRoot(
                        specProvider.GetReceiptSpec(block.Number), receipts, _fullReceiptDecoder);
                    if (block.Header.ReceiptsRoot != receiptRoot)
                        throw new EraVerificationException($"Mismatched receipt root at block {blockNumber}.");

                    int idx = (int)(block.Header.Number - startBlock);
                    blockMeta[idx] = (
                        block.Header.Hash!,
                        block.TotalDifficulty ?? UInt256.Zero,
                        !block.Header.IsPostMerge);
                }
            }, cancellation);
        }

        await Task.WhenAll(workers);

        // Accumulator verification applies to pre-merge and transition epochs only.
        // Post-merge-only epochs have no AccumulatorRoot entry.
        if (!e2.HasTotalDifficulty)
            return default;

        ValueHash256 storedRoot = ReadAccumulatorRoot();

        using AccumulatorCalculator calculator = new();
        foreach ((Hash256 hash, UInt256 td, bool isPreMerge) in blockMeta)
        {
            if (!isPreMerge) continue; // post-merge blocks excluded from accumulator even in transition epochs
            calculator.Add(hash, td);
        }

        ValueHash256 computedRoot = calculator.ComputeRoot();
        if (computedRoot != storedRoot)
            throw new EraVerificationException("Computed accumulator root does not match stored AccumulatorRoot.");

        // Optional chain-integrity check: verify stored root against externally trusted accumulators.
        if (validator is not null && !validator.VerifyAccumulator(startBlock, storedRoot))
            throw new EraVerificationException("Stored AccumulatorRoot does not match trusted accumulator.");

        return storedRoot;
    }

    public ValueHash256 CalculateChecksum() => e2.CalculateChecksum();

    public void Dispose() => e2.Dispose();

    private async Task<(Block, TxReceipt[])> ReadBlockAndReceipts(long blockNumber, CancellationToken cancellation)
    {
        long headerPos = e2.HeaderOffset(blockNumber);
        (BlockHeader header, _) = await e2.ReadSnappyCompressedEntryAndDecode(
            headerPos, DecodeHeader, EntryTypes.CompressedHeader, cancellation);

        // IsPostMerge is not RLP-serialized; restore it from Difficulty after decode.
        // Pre-merge blocks have Difficulty > 0, post-merge blocks have Difficulty == 0 (EIP-3675).
        header.IsPostMerge = header.Difficulty == 0;

        long bodyPos = e2.BodyOffset(blockNumber);
        (BlockBody body, _) = await e2.ReadSnappyCompressedEntryAndDecode(
            bodyPos, DecodeBody, EntryTypes.CompressedBody, cancellation);

        long receiptsPos = e2.SlimReceiptsOffset(blockNumber);
        (TxReceipt[] receipts, _) = await e2.ReadSnappyCompressedEntryAndDecode(
            receiptsPos, _slimReceiptDecoder.Decode, EntryTypes.CompressedSlimReceipts, cancellation);

        if (e2.HasTotalDifficulty)
        {
            long tdPos = e2.TotalDifficultyOffset(blockNumber);
            e2.ReadEntryAndDecode(
                tdPos,
                static buf => new UInt256(buf.Span, isBigEndian: false),
                EntryTypes.TotalDifficulty,
                out UInt256 td);
            header.TotalDifficulty = td;
        }

        // Bloom is not stored in slim receipts; TxReceipt.Bloom auto-calculates from Logs on first access.
        return (new Block(header, body), receipts);
    }

    private BlockHeader DecodeHeader(Memory<byte> buffer)
    {
        Rlp.ValueDecoderContext ctx = new(buffer.Span);
        return _headerDecoder.Decode(ref ctx)!;
    }

    private BlockBody DecodeBody(Memory<byte> buffer)
    {
        Rlp.ValueDecoderContext ctx = new(buffer.Span);
        return _blockBodyDecoder.Decode(ref ctx)!;
    }
}
