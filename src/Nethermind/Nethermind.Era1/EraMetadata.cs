// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
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

    public static EraMetadata CreateEraMetadata(MemoryMappedFile file)
    {
        var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        var blockIndex = BlockIndex.InitializeIndex(view);
        return new EraMetadata(blockIndex.Start, blockIndex.Count, view.Capacity, blockIndex);
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
        private readonly long _indexLength;
        private readonly MemoryMappedViewAccessor _index;

        public long Start => _start;
        public long Count => _count;

        public long SizeIncludingHeader =>  E2StoreStream.HeaderSize + CountValue + StartingBlockNumberValue + 8 * (int)_count;

        private BlockIndex(MemoryMappedViewAccessor index, long start, long count, long indexLength)
        {
            _index = index;
            _start = start;
            _count = count;
            _indexLength = indexLength;
        }

        public long BlockOffset(long blockNumber)
        {
            if (blockNumber > Start + Count || blockNumber < Start)
                throw new ArgumentOutOfRangeException(nameof(blockNumber), $"Block {blockNumber} is outside the bounds of this index.");

            int indexOffset = (int)(blockNumber - _start) * 8;
            int blockIndexOffset = 8 + indexOffset;
            long relativeOffset = _index.ReadInt64(_index.Capacity - _indexLength + blockIndexOffset);
            return _index.Capacity - SizeIncludingHeader + relativeOffset;
        }

        public static BlockIndex InitializeIndex(MemoryMappedViewAccessor view)
        {
            using ArrayPoolList<byte> pooledBytes = new(8, 8);
            long c = view.ReadInt64(view.Capacity - 8);
            int indexLength = CountValue + StartingBlockNumberValue + 8 * (int)c;

            if (indexLength < 16 || indexLength > EraWriter.MaxEra1Size * 8 + CountValue + StartingBlockNumberValue)
                throw new EraFormatException("Index is in an invalid format.");

            long s = view.ReadInt64(view.Capacity - indexLength);
            return new(view, s, c, indexLength);
        }
        private void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                _index.Dispose();
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
