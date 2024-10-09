// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Era1;

public class EraMetadata: IDisposable
{
    private readonly BlockIndex _blockIndex;
    public long Start { get; }
    public long End => Start + Count - 1;
    public long Count { get; }
    public long Length { get; }

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
        private bool _disposedValue;
        private readonly long _start;
        private readonly long _count;
        private readonly long _length;
        private readonly ArrayPoolList<byte> _index;

        public long Start => _start;
        public long Count => _count;

        private BlockIndex(Span<byte> index, long start, long count, long length)
        {
            try
            {
                _index = new ArrayPoolList<byte>(index.Length);
                index.CopyTo(_index.AsSpan(0, index.Length));
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
            long relativeOffset = BitConverter.ToInt64(_index.AsSpan(blockIndexOffset, 8));

            int indexLength = 16 + 8 * (int)_count;
            return _length - indexLength + blockIndexOffset + 8 + relativeOffset;
        }

        public static async Task<BlockIndex> InitializeIndex(Stream stream, CancellationToken cancellation)
        {
            using ArrayPoolList<byte> pooledBytes = new(8);
            Memory<byte> bytes = pooledBytes.AsMemory(0, 8);

            stream.Position = stream.Length - 8;
            await stream.ReadAsync(bytes, cancellation);
            long c = BitConverter.ToInt64(bytes.Span);

            int indexLength = 16 + 8 * (int)c;

            if (indexLength < 16 || indexLength > EraWriter.MaxEra1Size * 8 + 16)
                throw new EraFormatException("Index is in an invalid format.");

            using ArrayPoolList<byte> blockIndex = new(indexLength);
            blockIndex.AddRange(bytes.Span);

            long startIndex = stream.Length - indexLength;
            stream.Position = startIndex;
            await stream.ReadAsync(blockIndex.AsMemory(0, indexLength), cancellation);
            long s = BitConverter.ToInt64(blockIndex.AsSpan(0, 8));
            return new(blockIndex.AsSpan(0, indexLength), s, c, stream.Length);
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
