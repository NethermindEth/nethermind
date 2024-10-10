// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Collections;

namespace Nethermind.Era1;

public class EraMetadata: IDisposable
{
    private const int AccumulatorValueSize = 32;

    private readonly BlockIndex _blockIndex;
    public long Start { get; }
    public long End => Start + Count - 1;
    public long Count { get; }
    public long Length { get; }
    public long AccumulatorOffset => Length - (E2StoreStream.HeaderSize + AccumulatorValueSize + _blockIndex.SizeIncludingHeader);

    private EraMetadata(long start, long count, long length, BlockIndex blockIndex)
    {
        Start = start;
        Count = count;
        Length = length;

        _blockIndex = blockIndex;
    }

    public static async Task<EraMetadata> CreateEraMetadata(Stream stream, CancellationToken token)
    {
        var blockIndex = await BlockIndex.InitializeIndex(stream, token);
        return new EraMetadata(blockIndex.Start, blockIndex.Count, stream.Length, blockIndex);
    }

    public long BlockOffset(long blockNumber) => _blockIndex!.BlockOffset(blockNumber);

    public void Dispose()
    {
        _blockIndex.Dispose();
    }

    private sealed class BlockIndex : IDisposable
    {
        private const int CountValue = 8;
        private const int StartingBlockNumberValue = 8;

        private bool _disposedValue;
        private readonly long _start;
        private readonly long _count;
        private readonly long _length;
        private readonly ArrayPoolList<byte> _index;

        public long Start => _start;
        public long Count => _count;

        public long SizeIncludingHeader =>  E2StoreStream.HeaderSize + CountValue + StartingBlockNumberValue + 8 * (int)_count;

        private BlockIndex(Span<byte> index, long start, long count, long length)
        {
            try
            {
                _index = new ArrayPoolList<byte>(index.Length);
                index.CopyTo(_index.AsSpan()[..index.Length]);
            }
            catch
            {
                _index?.Dispose();
                throw;
            }
            _start = start;
            _count = count;
            _length = length;
        }

        public long BlockOffset(long blockNumber)
        {
            if (blockNumber > Start + Count || blockNumber < Start)
                throw new ArgumentOutOfRangeException(nameof(blockNumber), $"Block {blockNumber} is outside the bounds of this index.");

            int indexOffset = (int)(blockNumber - _start) * 8;
            int blockIndexOffset = 8 + indexOffset;
            long relativeOffset = BinaryPrimitives.ReadInt64LittleEndian(_index.AsSpan()[blockIndexOffset..8]);

            return _length - SizeIncludingHeader + relativeOffset;
        }

        public static async Task<BlockIndex> InitializeIndex(Stream stream, CancellationToken cancellation)
        {
            using ArrayPoolList<byte> pooledBytes = new(8);
            Memory<byte> bytes = pooledBytes.AsMemory()[..8];

            stream.Position = stream.Length - 8;
            await stream.ReadAsync(bytes, cancellation);
            long c = BinaryPrimitives.ReadInt64LittleEndian(bytes.Span);

            int indexLength = CountValue + StartingBlockNumberValue + 8 * (int)c;

            if (indexLength < 16 || indexLength > EraWriter.MaxEra1Size * 8 + CountValue + StartingBlockNumberValue)
                throw new EraFormatException("Index is in an invalid format.");

            long startIndex = stream.Length - indexLength;
            stream.Position = startIndex;

            using ArrayPoolList<byte> blockIndex = new(indexLength, indexLength);
            await stream.ReadAsync(blockIndex.AsMemory()[..indexLength], cancellation);
            long s = BinaryPrimitives.ReadInt64LittleEndian(blockIndex.AsSpan()[..8]);
            return new(blockIndex.AsSpan()[..indexLength], s, c, stream.Length);
        }
        private void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _index?.Dispose();
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
    }
}
