// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Era1;
internal class EraReader : IAsyncEnumerable<(Block, TxReceipt[], UInt256)>, IDisposable
{
    private bool _disposedValue;
    private long _currentBlockNumber;
    private HeaderDecoder _headerDecoder = new();
    private ReceiptMessageDecoder _receiptDecoder = new();
    private E2Store _store;
    private IByteBufferAllocator _byteBufferAllocator;

    public long CurrentBlockNumber => _currentBlockNumber;

    private EraReader(E2Store e2, IByteBufferAllocator byteBufferAllocator)
    {
        _store = e2;
        _byteBufferAllocator = byteBufferAllocator;
        Reset();
    }
    public async IAsyncEnumerator<(Block, TxReceipt[], UInt256)> GetAsyncEnumerator(CancellationToken cancellation = default)
    {
        Reset();
        EntryReadResult? result;
        while (true)
        {
            result = await Next(false, cancellation);
            if (result == null) break;
            yield return (result.Block, result.Receipts, result.TotalDifficulty);
        }
    }
    /// <summary>
    /// Verify that the accumulator matches the archive data. 
    /// </summary>
    /// <param name="cancellation"></param>
    /// <returns>Returns <see cref="true"/> if the data matches the accumulator, and <see cref="false"/> if there is no match.</returns>
    public async Task<bool> VerifyAccumulator(byte[] expectedAccumulator, IReceiptSpec receiptSpec, CancellationToken cancellation = default)
    {
        if (receiptSpec is null) throw new ArgumentNullException(nameof(receiptSpec));
        UInt256 currentTd = await CalculateStartingTotalDiffulty(cancellation);
        Reset();

        int actualCount = 0;
        EntryReadResult? result;
        AccumulatorCalculator calculator = new();
        while (true)
        {
            result = await Next(true, cancellation);
            if (result == null) break;

            if (result.Block.Header.Hash != result.ComputedHeaderHash)
            {
                return false;
            }
            Hash256 txRoot = new TxTrie(result.Block.Transactions).RootHash;
            if (result.Block.Header.TxRoot != txRoot)
            {
                return false;
            }
            Hash256 receiptRoot = new ReceiptTrie(receiptSpec, result.Receipts).RootHash;
            if (result.Block.Header.ReceiptsRoot != receiptRoot)
            {
                return false;
            }
            currentTd += result.Block.Difficulty;
            calculator.Add(result.Block.Header.Hash!, currentTd);
            actualCount++;
        }

        return Enumerable.SequenceEqual(expectedAccumulator, calculator.ComputeRoot().ToArray());
    }

    private async Task<UInt256> CalculateStartingTotalDiffulty(CancellationToken cancellation)
    {
        EntryReadResult? result = await ReadBlockAndReceipts(_store.Metadata.Start, true, cancellation);
        if (result == null) throw new EraException("Invalid Era1 archive format.");
        return result.TotalDifficulty - result.Block.Header.Difficulty;
    }

    public static Task<EraReader> Create(string file, IByteBufferAllocator? allocator = null, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(file)) throw new ArgumentException("Cannot be null or empty.", nameof(file));
        return Create(File.OpenRead(file), allocator, token);
    }
    public static async Task<EraReader> Create(Stream stream, IByteBufferAllocator? allocator = null, CancellationToken token = default)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead) throw new ArgumentException("Provided stream is not readable.");

        E2Store e2 = new E2Store(stream);
        await e2.SetMetaData(token);
        EraReader e = new EraReader(e2, allocator ?? PooledByteBufferAllocator.Default);

        return e;
    }
    // Format: <network>-<epoch>-<hexroot>.era1
    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network)
    {
        var entries = Directory.GetFiles(directoryPath, "*.era1");
        if (!entries.Any())
            yield break;

        uint next = 0;

        foreach (string file in entries)
        {
            string[] parts = Path.GetFileName(file).Split(new char[] { '-' });
            if (parts.Length != 3 || parts[0] != network)
            {
                continue;
            }
            uint epoch;
            if (!uint.TryParse(parts[1], out epoch))
                throw new EraException($"Invalid era1 filename: {Path.GetFileName(file)}");
            else if (epoch != next)
                throw new EraException($"Epoch {epoch} is missing.");

            next++;
            yield return (file);
        }
    }
    public async Task<byte[]> ReadAccumulator(CancellationToken cancellation = default)
    {
        _store.Seek(-32 - 8 * 4 - _store.Metadata.Count * 8, SeekOrigin.End);
        Entry accumulator = await _store.ReadEntryCurrentPosition(cancellation);
        CheckType(accumulator, EntryTypes.Accumulator);
        IByteBuffer buffer = _byteBufferAllocator.Buffer(32);
        try
        {
            await _store.ReadEntryValue(buffer, accumulator, cancellation);
            return buffer.ReadAllBytesAsArray();
        }
        finally
        {
            buffer.Release();
        }
    }
    public async Task<(Block, TxReceipt[], UInt256)> GetBlockByNumber(long number, CancellationToken cancellation = default)
    {
        if (number < _store.Metadata.Start)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be less than the first block number {_store.Metadata.Start}.");
        if (number > _store.Metadata.End)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be more than the last block number {_store.Metadata.End}.");
        EntryReadResult result = await ReadBlockAndReceipts(number, false, cancellation);
        return (result.Block, result.Receipts, result.TotalDifficulty);
    }
    private async Task<EntryReadResult?> Next(bool computeHeaderHash, CancellationToken cancellationToken)
    {
        if (_store.Metadata.Start + _store.Metadata.Count <= _currentBlockNumber)
        {
            //TODO test enumerate more than once
            Reset();
            return null;
        }
        EntryReadResult result = await ReadBlockAndReceipts(_currentBlockNumber, computeHeaderHash, cancellationToken);
        _currentBlockNumber++;
        return result;
    }

    private async Task<EntryReadResult> ReadBlockAndReceipts(long blockNumber, bool computeHeaderHash, CancellationToken cancellationToken)
    {
        if (blockNumber < _store.Metadata.Start
            || blockNumber > _store.Metadata.Start + _store.Metadata.Count)
            throw new ArgumentOutOfRangeException("Value is outside the range of the archive.", blockNumber, nameof(blockNumber));
        long blockOffset = await SeekToBlock(blockNumber, cancellationToken);
        IByteBuffer buffer = _byteBufferAllocator.Buffer(1024 * 1024);

        try
        {
            await ReadEntry(blockOffset, buffer, EntryTypes.CompressedHeader, cancellationToken);
            NettyRlpStream rlpStream = new NettyRlpStream(buffer);
            Hash256? currentComputedHeaderHash = null;
            if (computeHeaderHash)
                currentComputedHeaderHash = _headerDecoder.ComputeHeaderHash(rlpStream);
            BlockHeader header = Rlp.Decode<BlockHeader>(rlpStream);

            await ReadEntry(buffer, EntryTypes.CompressedBody, cancellationToken);
            BlockBody body = Rlp.Decode<BlockBody>(new NettyRlpStream(buffer));

            await ReadEntry(buffer, EntryTypes.CompressedReceipts, cancellationToken);
            TxReceipt[] receipts = DecodeReceipts(buffer);

            Entry e = await _store.ReadEntryCurrentPosition(cancellationToken);
            CheckType(e, EntryTypes.TotalDifficulty);
            await _store.ReadEntryValue(buffer, e, cancellationToken);

            UInt256 currentTotalDiffulty = new UInt256(buffer.ReadAllBytesAsSpan());

            Block block = new Block(header, body);

            return new EntryReadResult(block, receipts, currentTotalDiffulty, currentComputedHeaderHash);
        }
        finally
        {
            buffer.Release();
        };
    }

    private async Task ReadEntry(IByteBuffer buffer, ushort expectedType, CancellationToken cancellation)
    {
        Entry e = await _store.ReadEntryCurrentPosition(cancellation);
        CheckType(e, expectedType);
        await _store.ReadEntryValueAsSnappy(buffer, e, cancellation);
    }
    private async Task ReadEntry(long offset, IByteBuffer buffer, ushort expectedType, CancellationToken cancellation)
    {
        Entry e = await _store.ReadEntryAt(offset, cancellation);
        CheckType(e, expectedType);
        await _store.ReadEntryValueAsSnappy(buffer, e, cancellation);
    }

    private TxReceipt[] DecodeReceipts(IByteBuffer buf)
    {
        return _receiptDecoder.DecodeArray(new NettyRlpStream(buf));
    }

    private async Task<long> SeekToBlock(long blockNumber, CancellationToken token)
    {
        //Last 8 bytes is the count, so we skip them
        long startOfIndex = _store.Metadata.Length - 8 - _store.Metadata.Count * 8;
        long indexOffset = (blockNumber - _store.Metadata.Start) * 8;
        long blockIndexOffset = startOfIndex + indexOffset;

        long blockOffset = await _store.ReadValueAt(blockIndexOffset, token);
        return _store.Seek(blockOffset, SeekOrigin.Current);
    }

    private void Reset()
    {
        _currentBlockNumber = _store.Metadata.Start;
    }

    private static void CheckType(Entry e, ushort expected)
    {
        if (e.Type != expected) throw new EraException($"Expected an entry of type {expected}, but got {e.Type}.");
    }

    private static bool IsValidFilename(string file)
    {
        if (!Path.GetExtension(file).Equals(".era1", StringComparison.OrdinalIgnoreCase))
            return false;
        string[] parts = file.Split(new char[] { '-' });
        uint epoch;
        if (parts.Length != 3 || !uint.TryParse(parts[1], out epoch))
            return false;
        return true;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _store?.Dispose();
            }
            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private class EntryReadResult
    {
        public EntryReadResult(Block block, TxReceipt[] receipts, UInt256 totalDifficulty, Hash256? headerHash)
        {
            Block = block;
            TotalDifficulty = totalDifficulty;
            ComputedHeaderHash = headerHash;
            Receipts = receipts;
        }
        public Block Block { get; }
        public TxReceipt[] Receipts { get; }
        public UInt256 TotalDifficulty { get; }
        public Hash256? ComputedHeaderHash { get; }
    }
}
