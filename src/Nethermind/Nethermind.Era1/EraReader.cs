// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Era1.Exceptions;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Era1;

/// <summary>
/// Main reader for era file. Uses E2StoreReader which internally mmap the whole file. This reader is thread safe
/// allowing multiple thread to read from it at the same time.
/// </summary>
public class EraReader(E2StoreReader e2) : IAsyncEnumerable<(Block, TxReceipt[])>, IDisposable
{
    private readonly ReceiptMessageDecoder _receiptDecoder = new();
    private readonly IRlpDecoder<BlockBody> _blockBodyDecoder = Rlp.GetDecoderOrThrow<BlockBody>();
    private readonly IRlpDecoder<BlockHeader> _headerDecoder = Rlp.GetDecoderOrThrow<BlockHeader>();
    private readonly E2StoreReader _fileReader = e2;

    public ulong FirstBlock => _fileReader.First;
    public ulong LastBlock => _fileReader.LastBlock;

    public EraReader(string fileName) : this(new E2StoreReader(fileName))
    {
    }

    public async IAsyncEnumerator<(Block, TxReceipt[])> GetAsyncEnumerator(CancellationToken cancellation = default)
    {
        foreach (ulong blockNumber in EnumerateBlockNumber())
        {
            EntryReadResult result = await ReadBlockAndReceipts(blockNumber, false, BodyForm.Decoded, cancellation);
            yield return (result.Block, result.Receipts);
        }
    }

    private IEnumerable<ulong> EnumerateBlockNumber()
    {
        ulong blockNumber = _fileReader.First;
        while (blockNumber <= _fileReader.LastBlock)
        {
            yield return blockNumber;
            blockNumber++;
        }
    }

    /// <summary>
    /// Verify the content of this file. Notably, it verify that the accumulator match, but not necessarily trusted.
    /// It also validate the block with IBlockValidator. Ideally all verification is done here so that
    /// the file is easy to read directly without having to import.
    /// </summary>
    /// <param name="validateTransactions">
    /// Whether to decode the transactions and validate the blocks with <see cref="IBlockValidator.ValidateOrphanedBlock"/>.
    /// Without it, the body is checked against the header with its transactions kept encoded; this is enough when the
    /// accumulator is trusted, as the trusted header hashes then bind the bodies.
    /// </param>
    /// <param name="cancellation"></param>
    /// <returns>Returns <see cref="true"/> if the expected accumulator matches, and <see cref="false"/> if there is no match.</returns>
    public async Task<ValueHash256> VerifyContent(ISpecProvider specProvider, IBlockValidator blockValidator, int verifyConcurrency = 0, bool validateTransactions = true, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(specProvider);
        if (verifyConcurrency == 0) verifyConcurrency = Environment.ProcessorCount;

        ValueHash256 accumulator = ReadAccumulator();

        ulong startBlock = _fileReader.First;
        int blockCount = (int)_fileReader.BlockCount;
        using ArrayPoolList<(Hash256, UInt256)> blockHashes = new(blockCount, blockCount);

        ConcurrentQueue<ulong> blockNumbers = new(EnumerateBlockNumber());

        using ArrayPoolList<Task> workers = Enumerable.Range(0, verifyConcurrency).Select((_) => Task.Run(async () =>
        {
            while (blockNumbers.TryDequeue(out ulong blockNumber))
            {
                EntryReadResult? result = await ReadBlockAndReceipts(blockNumber, true, validateTransactions ? BodyForm.Decoded : BodyForm.EncodedTransactions, cancellation);
                EntryReadResult err = result.Value;

                if (err.Block.Header.Number != blockNumber)
                {
                    throw new EraVerificationException($"Mismatched header number {err.Block.Header.Number}. Block number {blockNumber}.");
                }

                if (!blockValidator.ValidateBodyAgainstHeader(err.Block.Header, err.Block.Body, out string? error))
                {
                    throw new EraVerificationException($"Mismatched block body against header: {error}. Block number {blockNumber}.");
                }

                if (validateTransactions && !blockValidator.ValidateOrphanedBlock(err.Block, out error))
                {
                    throw new EraVerificationException($"Invalid block {error}");
                }

                Hash256 receiptRoot = ReceiptTrie.CalculateRoot(specProvider.GetReceiptSpec(err.Block.Number), err.Receipts, _receiptDecoder);
                if (err.Block.Header.ReceiptsRoot != receiptRoot)
                {
                    throw new EraVerificationException($"Mismatched receipt root. Block number {blockNumber}.");
                }

                // Note: Header.Hash is calculated by HeaderDecoder.
                blockHashes[(int)(err.Block.Header.Number - startBlock)] = (err.Block.Header.Hash!, err.Block.TotalDifficulty!.Value);

                if (err.EncodedBody is not null)
                {
                    BlockBodyDecoder.ReturnTransactions(err.Block.Body);
                    err.EncodedBody.Dispose();
                }
            }
        }, cancellation)).ToPooledList(verifyConcurrency);
        await Task.WhenAll(workers.AsSpan());

        using AccumulatorCalculator calculator = new();
        foreach ((Hash256, UInt256) valueTuple in blockHashes.AsSpan())
        {
            calculator.Add(valueTuple.Item1, valueTuple.Item2);
        }

        if (accumulator != calculator.ComputeRoot())
        {
            throw new EraVerificationException("Computed accumulator does not match stored accumulator");
        }

        return accumulator;
    }

    public ValueHash256 ReadAccumulator()
    {
        _ = _fileReader.ReadEntryAndDecode<ValueHash256>(
            _fileReader.AccumulatorOffset,
            static (buffer) => new ValueHash256(buffer.Span),
            EntryTypes.Accumulator,
            out ValueHash256 hash);

        return hash;
    }

    /// <summary>
    /// Computes the accumulator root from the block hashes and total difficulties stored in this file.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="VerifyContent"/>, this does not validate blocks or receipts.
    /// Compare the result with <see cref="ReadAccumulator"/> to detect a stored accumulator that does not match the content.
    /// </remarks>
    internal async Task<ValueHash256> CalculateAccumulator(CancellationToken cancellation = default)
    {
        using AccumulatorCalculator calculator = new();
        foreach (ulong blockNumber in EnumerateBlockNumber())
        {
            EntryReadResult result = await ReadBlockAndReceipts(blockNumber, true, BodyForm.Decoded, cancellation);
            calculator.Add(result.Block.Header.Hash!, result.Block.TotalDifficulty!.Value);
        }

        return calculator.ComputeRoot();
    }

    public async Task<(Block, TxReceipt[])> GetBlockByNumber(ulong number, CancellationToken cancellation = default)
    {
        if (number < _fileReader.First)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be less than the first block number {_fileReader.First}. Number is {number}.");
        if (number > _fileReader.LastBlock)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be more than the last block number {_fileReader.LastBlock}. Number is {number}.");
        EntryReadResult result = await ReadBlockAndReceipts(number, false, BodyForm.Decoded, cancellation);
        return (result.Block, result.Receipts);
    }

    /// <summary>Reads a block, its body as stored in the archive, and the receipts.</summary>
    /// <remarks>Dispose the body when done.</remarks>
    public async Task<(Block Block, ArrayPoolList<byte> Body, TxReceipt[] Receipts)> GetEncodedBlockByNumber(ulong number, CancellationToken cancellation = default)
    {
        EntryReadResult result = await ReadBlockAndReceipts(number, false, BodyForm.DecodedAndEncoded, cancellation);
        return (result.Block, result.EncodedBody!, result.Receipts);
    }

    private async Task<EntryReadResult> ReadBlockAndReceipts(ulong blockNumber, bool computeHeaderHash, BodyForm bodyForm, CancellationToken cancellationToken)
    {
        if (blockNumber < _fileReader.First
            || blockNumber > _fileReader.LastBlock)
            throw new ArgumentOutOfRangeException(nameof(blockNumber), blockNumber, "Value is outside the range of the archive.");

        long position = _fileReader.BlockOffset(blockNumber);

        (BlockHeader header, long readSize) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            position,
            DecodeHeader,
            EntryTypes.CompressedHeader,
            cancellationToken);

        position += readSize;

        ((BlockBody body, ArrayPoolList<byte>? encodedBody), readSize) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            position,
            buffer => DecodeBody(buffer, bodyForm),
            EntryTypes.CompressedBody, cancellationToken);

        position += readSize;

        (TxReceipt[] receipts, readSize) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            position,
            DecodeReceipts,
            EntryTypes.CompressedReceipts, cancellationToken);

        position += readSize;

        _ = _fileReader.ReadEntryAndDecode(
            position,
            static (buffer) => new UInt256(buffer.Span, isBigEndian: false),
            EntryTypes.TotalDifficulty,
            out UInt256 currentTotalDifficulty);
        header.TotalDifficulty = currentTotalDifficulty;

        Block block = new(header, body);
        return new EntryReadResult(block, receipts, encodedBody);
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

    private BlockHeader DecodeHeader(Memory<byte> buffer)
    {
        RlpReader ctx = new(buffer.Span);
        return _headerDecoder.Decode(ref ctx)!;
    }

    private TxReceipt[] DecodeReceipts(Memory<byte> buffer)
    {
        RlpReader ctx = new(buffer.Span);
        // Era1 receipts are part of the block root commitment; reject empty-list placeholders.
        return ctx.DecodeNonNullArray<TxReceipt>(_receiptDecoder);
    }

    public ValueHash256 CalculateChecksum() => _fileReader.CalculateChecksum();

    public void Dispose() => _fileReader.Dispose();

    private struct EntryReadResult(Block block, TxReceipt[] receipts, ArrayPoolList<byte>? encodedBody)
    {
        public Block Block { get; } = block;
        public TxReceipt[] Receipts { get; } = receipts;
        public ArrayPoolList<byte>? EncodedBody { get; } = encodedBody;
    }
}
