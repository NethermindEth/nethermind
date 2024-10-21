// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.IO.Abstractions;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Era1;
public class EraReader : IAsyncEnumerable<(Block, TxReceipt[])>, IDisposable
{
    private bool _disposedValue;
    private ReceiptMessageDecoder _receiptDecoder = new();
    private BlockBodyDecoder _blockBodyDecoder = new();
    private E2StoreReader _fileReader;

    public EraReader(string fileName): this(new E2StoreReader(fileName))
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
    /// Verify that the accumulator matches the archive data.
    /// </summary>
    /// <param name="cancellation"></param>
    /// <returns>Returns <see cref="true"/> if the expected accumulator matches, and <see cref="false"/> if there is no match.</returns>
    public async Task<bool> VerifyAccumulator(ValueHash256 expectedAccumulator, ISpecProvider specProvider, CancellationToken cancellation = default)
    {
        if (specProvider is null) throw new ArgumentNullException(nameof(specProvider));

        UInt256? currentTd = null;
        bool isFirst = true;

        using AccumulatorCalculator calculator = new();

        foreach (var blockNumber in EnumerateBlockNumber())
        {
            EntryReadResult? result = await ReadBlockAndReceipts(blockNumber, true, cancellation);
            EntryReadResult err = result.Value;
            if (err.Block.Header.Hash != err.ComputedHeaderHash)
            {
                return false;
            }
            Hash256 txRoot = new TxTrie(err.Block.Transactions).RootHash;
            if (err.Block.Header.TxRoot != txRoot)
            {
                return false;
            }
            Hash256 receiptRoot = new ReceiptTrie<TxReceipt>(specProvider.GetReceiptSpec(err.Block.Number), err.Receipts, _receiptDecoder).RootHash;
            if (err.Block.Header.ReceiptsRoot != receiptRoot)
            {
                return false;
            }

            if (isFirst)
            {
                currentTd = (err.Block.TotalDifficulty - err.Block.Difficulty);
                isFirst = false;
            }

            currentTd += err.Block.Difficulty;
            err.Block.Header.TotalDifficulty = err.Block.TotalDifficulty;
            calculator.Add(err.Block.Header.Hash!, currentTd!.Value);
        }

        return expectedAccumulator == calculator.ComputeRoot();
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
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be less than the first block number {_fileReader.StartBlock}.");
        if (number > _fileReader.LastBlock)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be more than the last block number {_fileReader.LastBlock}.");
        EntryReadResult result = await ReadBlockAndReceipts(number, false, cancellation);
        return (result.Block, result.Receipts);
    }

    private async Task<EntryReadResult> ReadBlockAndReceipts(long blockNumber, bool computeHeaderHash, CancellationToken cancellationToken)
    {
        if (blockNumber < _fileReader.StartBlock
            || blockNumber > _fileReader.StartBlock + _fileReader.BlockCount)
            throw new ArgumentOutOfRangeException("Value is outside the range of the archive.", blockNumber, nameof(blockNumber));

        long position = _fileReader.BlockOffset(blockNumber);

        ((BlockHeader header, Hash256? currentComputedHeaderHash), long readSize) = await _fileReader.ReadSnappyCompressedEntryAndDecode<(BlockHeader, Hash256?)>(
            position,
            computeHeaderHash ? DecodeHeaderAndHash : DecodeHeaderButNoHash,
            EntryTypes.CompressedHeader,
            cancellationToken);

        position += readSize;

        (BlockBody body, readSize) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            position,
            DecodeBody,
            EntryTypes.CompressedBody, cancellationToken);

        position += readSize;

        (TxReceipt[] receipts, readSize)  = await _fileReader.ReadSnappyCompressedEntryAndDecode(
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
        return new EntryReadResult(block, receipts, currentComputedHeaderHash);
    }

    private BlockBody DecodeBody(IByteBuffer buffer)
    {
        var ctx = new Rlp.ValueDecoderContext(buffer.AsSpan());
        return _blockBodyDecoder.Decode(ref ctx)!;
    }

    (BlockHeader header, Hash256? currentComputedHeaderHash) DecodeHeaderAndHash(IByteBuffer buffer)
    {
        NettyRlpStream rlpStream = new NettyRlpStream(buffer);
        Hash256? currentComputedHeaderHash = Keccak.Compute(rlpStream.PeekNextItem());
        BlockHeader header = Rlp.Decode<BlockHeader>(rlpStream);
        return (header, currentComputedHeaderHash);
    }

    (BlockHeader header, Hash256? currentComputedHeaderHash) DecodeHeaderButNoHash(IByteBuffer buffer)
    {
        NettyRlpStream rlpStream = new NettyRlpStream(buffer);
        BlockHeader header = Rlp.Decode<BlockHeader>(rlpStream);
        return (header, null);
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
        public EntryReadResult(Block block, TxReceipt[] receipts, Hash256? headerHash)
        {
            Block = block;
            ComputedHeaderHash = headerHash;
            Receipts = receipts;
        }
        public Block Block { get; }
        public TxReceipt[] Receipts { get; }
        public Hash256? ComputedHeaderHash { get; }
    }
}
