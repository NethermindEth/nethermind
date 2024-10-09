// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.IO.Abstractions;
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
    private E2StoreStream _storeStream;
    private IByteBufferAllocator _byteBufferAllocator;
    private readonly bool _isDescendingOrder;

    public long CurrentBlockNumber => _currentBlockNumber;
    public EraMetadata EraMetadata { get; }

    private static readonly char[] separator = new char[] { '-' };

    private EraReader(E2StoreStream e2, IByteBufferAllocator byteBufferAllocator, bool descendingOrder)
    {
        _storeStream = e2;
        EraMetadata = e2.GetMetadata(default).GetAwaiter().GetResult();
        _byteBufferAllocator = byteBufferAllocator;
        _isDescendingOrder = descendingOrder;
        Reset(_isDescendingOrder);
    }
    public static Task<EraReader> Create(string file, in CancellationToken token = default)
    {
        return Create(file, new FileSystem(), token);
    }
    public static Task<EraReader> Create(string file, IFileSystem fileSystem, in CancellationToken token = default)
    {
        return Create(file, fileSystem, false, null, token);
    }
    public static Task<EraReader> Create(string file, IFileSystem? fileSystem, bool descendingOrder = false, IByteBufferAllocator? allocator = null, in CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(file)) throw new ArgumentException("Cannot be null or empty.", nameof(file));
        if (fileSystem == null)
            fileSystem = new FileSystem();
        return Create(fileSystem.File.OpenRead(file), descendingOrder, allocator, token);
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

        E2StoreStream e2 = await E2StoreStream.ForRead(stream, allocator ?? PooledByteBufferAllocator.Default, token);
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
    /// <returns>Returns <see cref="true"/> if the expected accumulator matches, and <see cref="false"/> if there is no match.</returns>
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
            Hash256 receiptRoot = new ReceiptTrie<TxReceipt>(specProvider.GetReceiptSpec(err.Block.Number), err.Receipts, _receiptDecoder).RootHash;
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
        EntryReadResult? result = await ReadBlockAndReceipts(EraMetadata.Start, true, cancellation);
        if (result == null) throw new EraException("Invalid Era1 archive format.");
        return result.Value.TotalDifficulty - result.Value.Block.Header.Difficulty;
    }

    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network)
    {
        return GetAllEraFiles(directoryPath, network, new FileSystem());
    }
    public static IEnumerable<string> GetAllEraFiles(string directoryPath, string network, IFileSystem fileSystem)
    {
        if (directoryPath is null) throw new ArgumentNullException(nameof(directoryPath));
        if (network is null) throw new ArgumentNullException(nameof(network));
        if (fileSystem is null) throw new ArgumentNullException(nameof(fileSystem));

        var entries = fileSystem.Directory.GetFiles(directoryPath, "*.era1", new EnumerationOptions() { RecurseSubdirectories = false, MatchCasing = MatchCasing.PlatformDefault });
        if (!entries.Any())
            yield break;

        uint next = 0;
        foreach (string file in entries)
        {
            // Format: <network>-<epoch>-<hexroot>.era1
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
            yield return file;
        }
    }
    public Task<byte[]> ReadAccumulator(CancellationToken cancellation = default)
    {
        _storeStream.Seek(EraMetadata.AccumulatorOffset, SeekOrigin.Begin);
        return _storeStream.ReadEntryAndDecode<byte[]>(
            (buffer) => buffer.ReadAllBytesAsArray(),
            EntryTypes.Accumulator, cancellation);
    }
    public async Task<(Block, TxReceipt[], UInt256)> GetBlockByNumber(long number, CancellationToken cancellation = default)
    {
        if (number < EraMetadata.Start)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be less than the first block number {EraMetadata.Start}.");
        if (number > EraMetadata.End)
            throw new ArgumentOutOfRangeException(nameof(number), $"Cannot be more than the last block number {EraMetadata.End}.");
        EntryReadResult result = await ReadBlockAndReceipts(number, false, cancellation);
        return (result.Block, result.Receipts, result.TotalDifficulty);
    }
    private async Task<EntryReadResult?> Next(bool computeHeaderHash, CancellationToken cancellationToken)
    {
        if (_isDescendingOrder)
        {
            if (EraMetadata.Start > _currentBlockNumber)
            {
                //TODO test enumerate more than once
                Reset(_isDescendingOrder);
                return null;
            }
        }
        else
        {
            if (EraMetadata.Start + EraMetadata.Count <= _currentBlockNumber)
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
        if (blockNumber < EraMetadata.Start
            || blockNumber > EraMetadata.Start + EraMetadata.Count)
            throw new ArgumentOutOfRangeException("Value is outside the range of the archive.", blockNumber, nameof(blockNumber));
        SeekToBlock(blockNumber);

        (BlockHeader header, Hash256? currentComputedHeaderHash) = await _storeStream.ReadSnappyCompressedEntryAndDecode<(BlockHeader, Hash256?)>(
            computeHeaderHash ? DecodeHeaderAndHash : DecodeHeaderButNoHash, EntryTypes.CompressedHeader, cancellationToken);

        BlockBody body = await _storeStream.ReadSnappyCompressedEntryAndDecode(
            (buffer) => Rlp.Decode<BlockBody>(new NettyRlpStream(buffer)),
            EntryTypes.CompressedBody, cancellationToken);

        TxReceipt[] receipts  = await _storeStream.ReadSnappyCompressedEntryAndDecode(
            (buffer) => DecodeReceipts(buffer),
            EntryTypes.CompressedReceipts, cancellationToken);

        UInt256 currentTotalDiffulty = await _storeStream.ReadEntryAndDecode(
            (buffer) => new UInt256(buffer.AsSpan(), isBigEndian: false),
            EntryTypes.TotalDifficulty, cancellationToken);

        Block block = new Block(header, body);

        return new EntryReadResult(block, receipts, currentTotalDiffulty, currentComputedHeaderHash);
    }

    (BlockHeader header, Hash256? currentComputedHeaderHash) DecodeHeaderAndHash(IByteBuffer buffer)
    {
        NettyRlpStream rlpStream = new NettyRlpStream(buffer);
        Hash256? currentComputedHeaderHash = rlpStream.ComputeNextItemHash();
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

    private long SeekToBlock(long blockNumber)
    {
        long blockOffset = EraMetadata.BlockOffset(blockNumber);
        long offset = blockOffset - _storeStream.Position;
        if (offset == 0)
            return 0;
        return _storeStream.Seek(offset, SeekOrigin.Current);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Reset(bool descendingOrder)
    {
        if (descendingOrder)
        {
            _currentBlockNumber = EraMetadata.Start + EraMetadata.Count - 1;
        }
        else
        {
            _currentBlockNumber = EraMetadata.Start;
        }
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
                _storeStream?.Dispose();
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
