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
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Era1;

/// <summary>
/// Main reader for era file. Uses E2StoreReader which internally mmap the whole file. This reader is thread safe
/// allowing multiple thread to read from it at the same time.
/// </summary>
public class EraReader : IAsyncEnumerable<(Block, TxReceipt[])>, IDisposable
{
    private readonly ReceiptMessageDecoder _receiptDecoder = new();
    private readonly BlockBodyDecoder _blockBodyDecoder = BlockBodyDecoder.Instance;
    private readonly HeaderDecoder _headerDecoder = new();
    private readonly E2StoreReader _fileReader;

    public long FirstBlock => _fileReader.First;
    public long LastBlock => _fileReader.LastBlock;


    public EraReader(string fileName) : this(new E2StoreReader(fileName))
    {
    }


    public EraReader(E2StoreReader e2)
    {
        _fileReader = e2;
    }

    public async IAsyncEnumerator<(Block, TxReceipt[])> GetAsyncEnumerator(CancellationToken cancellation = default)
    {
        foreach (var blockNumber in EnumerateBlockNumber())
        {
            EntryReadResult result = await ReadBlockAndReceipts(blockNumber, false, cancellation);
            yield return (result.Block, result.Receipts);
        }
    }

    private IEnumerable<long> EnumerateBlockNumber()
    {
        long blockNumber = _fileReader.First;
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
    /// <param name="cancellation"></param>
    /// <returns>Returns <see cref="true"/> if the expected accumulator matches, and <see cref="false"/> if there is no match.</returns>
    public async Task<ValueHash256> VerifyContent(ISpecProvider specProvider, IBlockValidator blockValidator, int verifyConcurrency = 0, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(specProvider);
        if (verifyConcurrency == 0) verifyConcurrency = Environment.ProcessorCount;

        ValueHash256 accumulator = ReadAccumulator();

        long startBlock = _fileReader.First;
        int blockCount = (int)_fileReader.BlockCount;
        using ArrayPoolList<(Hash256, UInt256)> blockHashes = new(blockCount, blockCount);

        ConcurrentQueue<long> blockNumbers = new ConcurrentQueue<long>(EnumerateBlockNumber());

        using ArrayPoolList<Task> workers = Enumerable.Range(0, verifyConcurrency).Select((_) => Task.Run(async () =>
        {
            while (blockNumbers.TryDequeue(out long blockNumber))
            {
                EntryReadResult? result = await ReadBlockAndReceipts(blockNumber, true, cancellation);
                EntryReadResult err = result.Value;

                if (!blockValidator.ValidateBodyAgainstHeader(err.Block.Header, err.Block.Body, out string? error))
                {
                    throw new EraVerificationException($"Mismatched block body againts header: {error}. Block number {blockNumber}.");
                }

                if (!blockValidator.ValidateOrphanedBlock(err.Block, out error))
                {
                    throw new EraVerificationException($"Invalid block {error}");
                }

                Hash256 receiptRoot = new ReceiptTrie<TxReceipt>(specProvider.GetReceiptSpec(err.Block.Number),
                    err.Receipts, _receiptDecoder).RootHash;
                if (err.Block.Header.ReceiptsRoot != receiptRoot)
                {
                    throw new EraVerificationException($"Mismatched receipt root. Block number {blockNumber}.");
                }


                // Note: Header.Hash is calculated by HeaderDecoder.
                blockHashes[(int)(err.Block.Header.Number - startBlock)] = (err.Block.Header.Hash!, err.Block.TotalDifficulty!.Value);
            }
        }, cancellation)).ToPooledList(verifyConcurrency);
        await Task.WhenAll(workers.AsSpan());

        using AccumulatorCalculator calculator = new();
        foreach (var valueTuple in blockHashes.AsSpan())
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

    public async Task<(Block, TxReceipt[])> GetBlockByNumber(long number, CancellationToken cancellation = default)
    {
        if (number < _fileReader.First)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be less than the first block number {_fileReader.First}. Number is {number}.");
        if (number > _fileReader.LastBlock)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be more than the last block number {_fileReader.LastBlock}. Number is {number}.");
        EntryReadResult result = await ReadBlockAndReceipts(number, false, cancellation);
        return (result.Block, result.Receipts);
    }

    private async Task<EntryReadResult> ReadBlockAndReceipts(long blockNumber, bool computeHeaderHash, CancellationToken cancellationToken)
    {
        if (blockNumber < _fileReader.First
            || blockNumber > _fileReader.First + _fileReader.BlockCount)
            throw new ArgumentOutOfRangeException("Value is outside the range of the archive.", blockNumber, nameof(blockNumber));

        long position = _fileReader.BlockOffset(blockNumber);

        (BlockHeader header, long readSize) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            position,
            DecodeHeader,
            EntryTypes.CompressedHeader,
            cancellationToken);

        position += readSize;

        (BlockBody body, readSize) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            position,
            DecodeBody,
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
            out UInt256 currentTotalDiffulty);
        header.TotalDifficulty = currentTotalDiffulty;

        Block block = new Block(header, body);
        return new EntryReadResult(block, receipts);
    }

    private BlockBody DecodeBody(Memory<byte> buffer)
    {
        var ctx = new Rlp.ValueDecoderContext(buffer.Span);
        return _blockBodyDecoder.Decode(ref ctx)!;
    }

    private BlockHeader DecodeHeader(Memory<byte> buffer)
    {
        var ctx = new Rlp.ValueDecoderContext(buffer.Span);
        return _headerDecoder.Decode(ref ctx)!;
    }

    private TxReceipt[] DecodeReceipts(Memory<byte> buffer)
    {
        Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(buffer.Span);
        return RlpDecoderExtensions.DecodeArray(_receiptDecoder, ref ctx, RlpBehaviors.None);
    }

    public ValueHash256 CalculateChecksum()
    {
        return _fileReader.CalculateChecksum();
    }

    public void Dispose() => _fileReader.Dispose();

    private struct EntryReadResult
    {
        public EntryReadResult(Block block, TxReceipt[] receipts)
        {
            Block = block;
            Receipts = receipts;
        }
        public Block Block { get; }
        public TxReceipt[] Receipts { get; }
    }
}
