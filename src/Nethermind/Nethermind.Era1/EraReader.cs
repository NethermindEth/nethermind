// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
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
public class EraReader : IAsyncEnumerable<(Block, TxReceipt[], UInt256)>, IDisposable
{
    private bool _disposedValue;
    private long _currentBlockNumber;
    private ReceiptMessageDecoder _receiptDecoder = new();
    private E2Store _store;
    private IByteBufferAllocator _byteBufferAllocator;
    private readonly bool _isDescendingOrder;

    public long CurrentBlockNumber => _currentBlockNumber;
    public EraMetadata EraMetadata => _store.Metadata;

    private static readonly char[] separator = new char[] { '-' };

    private EraReader(E2Store e2, IByteBufferAllocator byteBufferAllocator, bool descendingOrder)
    {
        _store = e2;
        _byteBufferAllocator = byteBufferAllocator;
        _isDescendingOrder = descendingOrder;
        Reset(_isDescendingOrder);
    }
    public static Task<EraReader> Create(string file, in CancellationToken token = default)
    {
        return Create(file, null, token);
    }
    public static Task<EraReader> Create(string file, IByteBufferAllocator? allocator, in CancellationToken token = default)
    {
        return Create(file, false, allocator, token);
    }
    public static Task<EraReader> Create(string file, bool descendingOrder, in CancellationToken token = default)
    {
        return Create(file, descendingOrder, null, token);
    }
    public static Task<EraReader> Create(string file, bool descendingOrder, IByteBufferAllocator? allocator = null, in CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(file)) throw new ArgumentException("Cannot be null or empty.", nameof(file));

        return Create(File.OpenRead(file), descendingOrder, allocator, token);
    }
    public static Task<EraReader> Create(Stream stream, CancellationToken token = default)
    {
        return Create(stream, false, null, token);
    }
    public static Task<EraReader> Create(Stream stream, IByteBufferAllocator? allocator, CancellationToken token = default)
    {
        return Create(stream, false, allocator, token);
    }
    public static async Task<EraReader> Create(Stream stream, bool descendingOrder, IByteBufferAllocator? allocator = null, CancellationToken token = default)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead) throw new ArgumentException("Provided stream is not readable.", nameof(stream));

        E2Store e2 = await E2Store.ForRead(stream, token);
        EraReader e = new EraReader(e2, allocator ?? PooledByteBufferAllocator.Default, descendingOrder);

        return e;
    }
    public async IAsyncEnumerator<(Block, TxReceipt[], UInt256)> GetAsyncEnumerator(CancellationToken cancellation = default)
    {
        Reset(_isDescendingOrder);
        EntryReadResult? result;
        while (true)
        {
            result = await Next(false, cancellation);
            if (result == null) break;
            yield return (result.Value.Block, result.Value.Receipts, result.Value.TotalDifficulty);
        }
    }
    /// <summary>
    /// Verify that the accumulator matches the archive data. 
    /// </summary>
    /// <param name="cancellation"></param>
    /// <returns>Returns <see cref="true"/> if the data matches the accumulator, and <see cref="false"/> if there is no match.</returns>
    public async Task<bool> VerifyAccumulator(byte[] expectedAccumulator, ISpecProvider specProvider, CancellationToken cancellation = default)
    {
        if (specProvider is null) throw new ArgumentNullException(nameof(specProvider));
        UInt256 currentTd = await CalculateStartingTotalDiffulty(cancellation);
        Reset(false);

        int actualCount = 0;
        using AccumulatorCalculator calculator = new();
        while (true)
        {
            EntryReadResult? result = await Next(true, cancellation);
            if (result == null) break;
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
            Hash256 receiptRoot = new ReceiptTrie(specProvider.GetReceiptSpec(err.Block.Number), err.Receipts).RootHash;
            if (err.Block.Header.ReceiptsRoot != receiptRoot)
            {
                return false;
            }
            currentTd += err.Block.Difficulty;
            calculator.Add(err.Block.Header.Hash!, currentTd);
            actualCount++;
        }

        return Enumerable.SequenceEqual(expectedAccumulator, calculator.ComputeRoot().ToArray());
    }

    private async Task<UInt256> CalculateStartingTotalDiffulty(CancellationToken cancellation)
    {
        EntryReadResult? result = await ReadBlockAndReceipts(_store.Metadata.Start, true, cancellation);
        if (result == null) throw new EraException("Invalid Era1 archive format.");
        return result.Value.TotalDifficulty - result.Value.Block.Header.Difficulty;
    }

    // Format: <network>-<epoch>-<hexroot>.era1
    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network)
    {
        if (directoryPath is null) throw new ArgumentNullException(nameof(directoryPath));
        if (network is null) throw new ArgumentNullException(nameof(network));

        var entries = Directory.GetFiles(directoryPath, "*.era1", new EnumerationOptions() { RecurseSubdirectories=false, MatchCasing=MatchCasing.PlatformDefault });
        if (!entries.Any())
            yield break;

        uint next = 0;
        foreach (string file in entries)
        {
            string[] parts = Path.GetFileName(file).Split(separator);
            if (parts.Length != 3 || !network.Equals(parts[0], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            uint epoch;
            if (!uint.TryParse(parts[1], out epoch))
                throw new EraException($"Invalid era1 filename: {Path.GetFileName(file)}");
            //else if (epoch != next)
            //    throw new EraException($"Epoch {epoch} is missing.");

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
        if (_isDescendingOrder)
        {
            if (_store.Metadata.Start  > _currentBlockNumber)
            {
                //TODO test enumerate more than once
                Reset(_isDescendingOrder);
                return null;
            }
        }
        else
        {
            if (_store.Metadata.Start + _store.Metadata.Count <= _currentBlockNumber)
            {
                //TODO test enumerate more than once
                Reset(_isDescendingOrder);
                return null;
            }
        }
        
        EntryReadResult result = await ReadBlockAndReceipts(_currentBlockNumber, computeHeaderHash, cancellationToken);
        if (_isDescendingOrder)
            _currentBlockNumber--;
        else
            _currentBlockNumber++;
        return result;
    }

    private async Task<EntryReadResult> ReadBlockAndReceipts(long blockNumber, bool computeHeaderHash, CancellationToken cancellationToken)
    {
        if (blockNumber < _store.Metadata.Start
            || blockNumber > _store.Metadata.Start + _store.Metadata.Count)
            throw new ArgumentOutOfRangeException("Value is outside the range of the archive.", blockNumber, nameof(blockNumber));
        long seeked = SeekToBlock(blockNumber);
        //Worst case scenario buffer 
        IByteBuffer buffer = _byteBufferAllocator.Buffer(1024 * 1024 * 2);

        try
        {
            await ReadEntryHere(buffer, EntryTypes.CompressedHeader, cancellationToken);
            NettyRlpStream rlpStream = new NettyRlpStream(buffer);
            Hash256? currentComputedHeaderHash = null;
            if (computeHeaderHash)
                currentComputedHeaderHash = rlpStream.ComputeNextItemHash();
            BlockHeader header = Rlp.Decode<BlockHeader>(rlpStream);

            await ReadEntryHere(buffer, EntryTypes.CompressedBody, cancellationToken);

            //void Test()
            //{
            //    NettyBufferMemoryOwner memoryOwner = new(buffer);
            //    Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);

            //    var x = new BlockBodyDecoder();
            //    x.Decode(ref ctx);
            //}
            //Test();


            BlockBody body = Rlp.Decode<BlockBody>(new NettyRlpStream(buffer));

            await ReadEntryHere(buffer, EntryTypes.CompressedReceipts, cancellationToken);
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

    /// <summary>
    /// Reads an entry and loads it's value into <paramref name="buffer"/> from the current position in the stream.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="expectedType"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    private async Task ReadEntryHere(IByteBuffer buffer, ushort expectedType, CancellationToken cancellation)
    {
        Entry e = await _store.ReadEntryCurrentPosition(cancellation);
        CheckType(e, expectedType);
        await _store.ReadEntryValueAsSnappy(buffer, e, cancellation);
    }

    private TxReceipt[] DecodeReceipts(IByteBuffer buf)
    {
        return _receiptDecoder.DecodeArray(new NettyRlpStream(buf));
    }

    private long SeekToBlock(long blockNumber)
    {
        long offset = _store.BlockOffset(blockNumber) - _store.Position;
        if (offset == 0)
            return 0;
        return _store.Seek(offset, SeekOrigin.Current);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Reset(bool descendingOrder)
    {
        if (descendingOrder)
        {
            _currentBlockNumber = _store.Metadata.Start + _store.Metadata.Count - 1;
        }
        else
        {
            _currentBlockNumber = _store.Metadata.Start;
        }
    }
    [MethodImpl(MethodImplOptions. AggressiveInlining)]
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

    private struct EntryReadResult
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
