// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.EraE.E2Store;
using AccumulatorCalculator = Nethermind.Era1.AccumulatorCalculator;
using EraException = Nethermind.Era1.Exceptions.EraException;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;
using Nethermind.EraE.Proofs;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.EraE.Archive;

public sealed class EraReader(E2StoreReader e2) : IAsyncEnumerable<(Block, TxReceipt[])>, IDisposable
{
    private readonly EraSlimReceiptDecoder _slimReceiptDecoder = new();
    private readonly ReceiptMessageDecoder _fullReceiptDecoder = new();
    private readonly IRlpDecoder<BlockBody> _blockBodyDecoder = Rlp.GetDecoderOrThrow<BlockBody>();
    private readonly IRlpDecoder<BlockHeader> _headerDecoder = Rlp.GetDecoderOrThrow<BlockHeader>();

    public ulong FirstBlock => e2.First;
    public ulong LastBlock => e2.LastBlock;

    public EraReader(string fileName) : this(new E2StoreReader(fileName)) { }

    public async IAsyncEnumerator<(Block, TxReceipt[])> GetAsyncEnumerator(CancellationToken cancellation = default)
    {
        for (ulong blockNumber = e2.First; blockNumber <= e2.LastBlock; blockNumber++)
        {
            (Block block, _, TxReceipt[] receipts) = await ReadBlockAndReceipts(blockNumber, BodyForm.Decoded, cancellation);
            yield return (block, receipts);
        }
    }

    public async Task<(Block, TxReceipt[])> GetBlockByNumber(ulong number, CancellationToken cancellation = default)
    {
        if (number < e2.First)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be less than first block {e2.First}.");
        if (number > e2.LastBlock)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot exceed last block {e2.LastBlock}.");

        (Block block, _, TxReceipt[] receipts) = await ReadBlockAndReceipts(number, BodyForm.Decoded, cancellation);
        return (block, receipts);
    }

    /// <summary>Reads a block, its body as stored in the archive, and the receipts.</summary>
    /// <remarks>Dispose the body when done.</remarks>
    public async Task<(Block Block, ArrayPoolList<byte> Body, TxReceipt[] Receipts)> GetEncodedBlockByNumber(ulong number, CancellationToken cancellation = default)
    {
        if (number < e2.First)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be less than first block {e2.First}.");
        if (number > e2.LastBlock)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot exceed last block {e2.LastBlock}.");

        (Block block, ArrayPoolList<byte>? body, TxReceipt[] receipts) = await ReadBlockAndReceipts(number, BodyForm.DecodedAndEncoded, cancellation);
        return (block, body!, receipts);
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

        ulong startBlock = e2.First;
        int blockCount = (int)e2.BlockCount;

        (Hash256 Hash, UInt256 Td, bool IsPreMerge)[] blockMeta =
            new (Hash256 Hash, UInt256 Td, bool IsPreMerge)[blockCount];

        ConcurrentQueue<ulong> blockNumbers = new();
        for (ulong n = startBlock; n <= e2.LastBlock; n++)
        {
            blockNumbers.Enqueue(n);
        }

        Task[] workers = new Task[verifyConcurrency];
        for (int i = 0; i < verifyConcurrency; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                while (blockNumbers.TryDequeue(out ulong blockNumber))
                {
                    // The body is only checked against the header, which the encoded transactions are enough for.
                    (Block block, ArrayPoolList<byte>? encodedBody, TxReceipt[] receipts) = await ReadBlockAndReceipts(blockNumber, BodyForm.EncodedTransactions, cancellation);

                    if (block.Header.Number != blockNumber)
                        throw new EraVerificationException($"Mismatched header number {block.Header.Number}. Block {blockNumber}.");

                    if (!blockValidator.ValidateBodyAgainstHeader(block.Header, block.Body, out string? error))
                        throw new EraVerificationException($"Mismatched block body against header: {error}. Block {blockNumber}.");

                    Hash256 receiptRoot = ReceiptTrie.CalculateRoot(
                        specProvider.GetSpec(block.Header), receipts, _fullReceiptDecoder);
                    if (block.Header.ReceiptsRoot != receiptRoot)
                        throw new EraVerificationException($"Mismatched receipt root at block {blockNumber}.");

                    // Safe: each dequeued blockNumber is unique, so each idx is written by exactly one worker.
                    int idx = (int)(block.Header.Number - startBlock);
                    blockMeta[idx] = (
                        block.Header.Hash!,
                        block.TotalDifficulty ?? UInt256.Zero,
                        !block.Header.IsPostMerge);

                    if (encodedBody is not null)
                    {
                        BlockBodyDecoder.ReturnTransactions(block.Body);
                        encodedBody.Dispose();
                    }
                }
            }, cancellation);
        }

        await Task.WhenAll(workers);

        // Accumulator verification applies to pre-merge and transition epochs only.
        // Post-merge-only epochs have no AccumulatorRoot entry.
        // TODO: verify HistoricalRoots / HistoricalSummaries beacon proofs for post-merge epochs.
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

    private async Task<(Block, ArrayPoolList<byte>?, TxReceipt[])> ReadBlockAndReceipts(ulong blockNumber, BodyForm bodyForm, CancellationToken cancellation)
    {
        long headerPos = e2.HeaderOffset(blockNumber);
        (BlockHeader header, _) = await e2.ReadSnappyCompressedEntryAndDecode(
            headerPos, DecodeHeader, EntryTypes.CompressedHeader, cancellation);

        // IsPostMerge is not RLP-serialized; restore it from Difficulty after decode.
        // Pre-merge blocks have Difficulty > 0, post-merge blocks have Difficulty == 0 (EIP-3675).
        header.IsPostMerge = header.Difficulty == 0;

        long bodyPos = e2.BodyOffset(blockNumber);
        ((BlockBody body, ArrayPoolList<byte>? encodedBody), _) = await e2.ReadSnappyCompressedEntryAndDecode(
            bodyPos, buffer => DecodeBody(buffer, bodyForm), EntryTypes.CompressedBody, cancellation);

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
        return (new Block(header, body), encodedBody, receipts);
    }

    private BlockHeader DecodeHeader(Memory<byte> buffer)
    {
        RlpReader ctx = new(buffer.Span);
        return _headerDecoder.Decode(ref ctx)!;
    }

    /// <summary>How a body is read.</summary>
    private enum BodyForm
    {
        /// <summary>Decoded in full.</summary>
        Decoded,

        /// <summary>Decoded in full, with its encoding kept so that it can be stored as read.</summary>
        DecodedAndEncoded,

        /// <summary>
        /// With its transactions kept encoded, which is enough to check the body against the header. The pooled
        /// transactions refer to the kept encoding.
        /// </summary>
        EncodedTransactions,
    }

    // The entry buffer is pooled and reused after this returns, so a kept encoding gets its own pooled copy.
    private (BlockBody, ArrayPoolList<byte>?) DecodeBody(Memory<byte> buffer, BodyForm form)
    {
        if (form == BodyForm.EncodedTransactions && _blockBodyDecoder is BlockBodyDecoder decoder)
        {
            ArrayPoolList<byte> encoded = CopyOf(buffer.Span);
            return (decoder.DecodeWithEncodedTransactions(encoded.AsMemory())!, encoded);
        }

        RlpReader reader = new(buffer.Span);
        BlockBody body = _blockBodyDecoder.Decode(ref reader, RlpBehaviors.SkipPooledTransactions)!;
        return (body, form == BodyForm.DecodedAndEncoded ? CopyOf(buffer.Span) : null);

        static ArrayPoolList<byte> CopyOf(ReadOnlySpan<byte> bytes)
        {
            ArrayPoolList<byte> copy = new(bytes.Length, bytes.Length);
            bytes.CopyTo(copy.AsSpan());
            return copy;
        }
    }
}
