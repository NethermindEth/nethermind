// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using DotNetty.Buffers;
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
    private bool _disposedValue;
    private readonly ReceiptMessageDecoder _receiptDecoder = new();
    private readonly BlockBodyDecoder _blockBodyDecoder = BlockBodyDecoder.Instance;
    private readonly HeaderDecoder _headerDecoder = new();
    private readonly E2StoreReader _fileReader;

    public long StartBlock => _fileReader.StartBlock;
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
        long blockNumber = _fileReader.StartBlock;
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
    public async Task<ValueHash256> VerifyContent(ISpecProvider specProvider, IBlockValidator blockValidator, CancellationToken cancellation = default)
    {
        if (specProvider is null) throw new ArgumentNullException(nameof(specProvider));

        ValueHash256 accumulator = ReadAccumulator();

        long startBlock = _fileReader.StartBlock;
        int blockCount = (int)_fileReader.BlockCount;
        using ArrayPoolList<(Hash256, UInt256)> blockHashes = new(blockCount, blockCount);

        ConcurrentQueue<long> blockNumbers = new ConcurrentQueue<long>(EnumerateBlockNumber());

        Task[] workers = Enumerable.Range(0, Environment.ProcessorCount).Select((_) => Task.Run(async () =>
        {
            while (blockNumbers.TryDequeue(out long blockNumber))
            {
                EntryReadResult? result = await ReadBlockAndReceipts(blockNumber, true, cancellation);
                EntryReadResult err = result.Value;

                UInt256? totalDifficulty = err.Block.TotalDifficulty;
                err.Block.Header.TotalDifficulty = null;
                if (!blockValidator.ValidateSuggestedBlock(err.Block, out string? blockValidationErr))
                {
                    throw new EraVerificationException($"Invalid block {blockValidationErr}");
                }
                err.Block.Header.TotalDifficulty = totalDifficulty;

                Hash256 receiptRoot = new ReceiptTrie<TxReceipt>(specProvider.GetReceiptSpec(err.Block.Number),
                    err.Receipts, _receiptDecoder).RootHash;
                if (err.Block.Header.ReceiptsRoot != receiptRoot)
                {
                    throw new EraVerificationException($"Mismatched receipt root. Block number {blockNumber}.");
                }


                // Note: Header.Hash is calculated by HeaderDecoder.
                blockHashes[(int)(err.Block.Header.Number - startBlock)] = (err.Block.Header.Hash!, err.Block.TotalDifficulty!.Value);
            }
        }, cancellation)).ToArray();
        await Task.WhenAll(workers);

        using AccumulatorCalculator calculator = new();
        foreach (var valueTuple in blockHashes)
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
        return _fileReader.ReadEntryAndDecode<ValueHash256>(
            _fileReader.AccumulatorOffset,
            (buffer) => new ValueHash256(buffer.ReadAllBytesAsArray()),
            EntryTypes.Accumulator).Item1;
    }

    public async Task<(Block, TxReceipt[])> GetBlockByNumber(long number, CancellationToken cancellation = default)
    {
        if (number < _fileReader.StartBlock)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be less than the first block number {_fileReader.StartBlock}. Number is {number}.");
        if (number > _fileReader.LastBlock)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be more than the last block number {_fileReader.LastBlock}. Number is {number}.");
        EntryReadResult result = await ReadBlockAndReceipts(number, false, cancellation);
        return (result.Block, result.Receipts);
    }

    private async Task<EntryReadResult> ReadBlockAndReceipts(long blockNumber, bool computeHeaderHash, CancellationToken cancellationToken)
    {
        if (blockNumber < _fileReader.StartBlock
            || blockNumber > _fileReader.StartBlock + _fileReader.BlockCount)
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

        (UInt256 currentTotalDiffulty, readSize) = _fileReader.ReadEntryAndDecode(
            position,
            (buffer) => new UInt256(buffer.AsSpan(), isBigEndian: false),
            EntryTypes.TotalDifficulty);
        header.TotalDifficulty = currentTotalDiffulty;

        Block block = new Block(header, body);
        return new EntryReadResult(block, receipts);
    }

    private BlockBody DecodeBody(IByteBuffer buffer)
    {
        var ctx = new Rlp.ValueDecoderContext(buffer.AsSpan());
        return _blockBodyDecoder.Decode(ref ctx)!;
    }

    private BlockHeader DecodeHeader(IByteBuffer buffer)
    {
        var ctx = new Rlp.ValueDecoderContext(buffer.AsSpan());
        return _headerDecoder.Decode(ref ctx)!;
    }

    private TxReceipt[] DecodeReceipts(IByteBuffer buf)
    {
        return _receiptDecoder.DecodeArray(new NettyRlpStream(buf));
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            _fileReader.Dispose();
            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

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
