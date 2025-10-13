// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
namespace Nethermind.EraE;

public class E2StoreReader : Era1.E2StoreReader 
{
    private const int IndexSectionComponentsCount = 8;
    private long _componentsCount;
    private long _indexLength;

    public E2StoreReader(string filePath) : base(filePath) {}

    // return tuple of header, body, receipts positions in the file
    public new (long headerPosition, long bodyPosition, long receiptsPosition, long? totalDifficultyPosition) BlockOffset(long blockNumber)
    {
        EnsureIndexAvailable();

        if (blockNumber > _startBlock + _blockCount || blockNumber < _startBlock)
            throw new ArgumentOutOfRangeException(nameof(blockNumber), $"Block {blockNumber} is outside the bounds of this index.");

        long componentIndexLocation = (long)(blockNumber - _startBlock!) * _componentsCount * IndexOffsetSize;
        long offsetLocation = _indexLength - componentIndexLocation;
        
        long indexPosition = _fileLength - HeaderSize - _indexLength;
        
        // 3 consecutive bytes in the index are the header, body, and receipts offsets. All of them are relative to the index (i.e. negative values).
        long headerRelativeOffset = ReadInt64(_fileLength - offsetLocation);
        long bodyRelativeOffset = ReadInt64(_fileLength - offsetLocation + IndexOffsetSize);
        long receiptsRelativeOffset = ReadInt64(_fileLength - offsetLocation + IndexOffsetSize * 2);
        
        long? totalDifficultyPosition = null;
        if (_componentsCount > 3) {
            long tdRelativeOffset = ReadInt64(_fileLength - offsetLocation + IndexOffsetSize * 3);
            totalDifficultyPosition = indexPosition + tdRelativeOffset;
        }
        
        
        return (
            indexPosition + headerRelativeOffset, 
            indexPosition + bodyRelativeOffset, 
            indexPosition + receiptsRelativeOffset,
            totalDifficultyPosition
        );
    }

    protected override void EnsureIndexAvailable()
    {
        if (_startBlock != null) return;

        if (_fileLength < 32) throw new EraFormatException("Invalid era file. Too small to contain index.");

        // Read the block count
        _blockCount = (long)ReadUInt64(_fileLength - IndexSectionCount);
        // Read the components count. Per spec values is in range 3-5, so we need to read the value and use in the calculation.
        _componentsCount = (long)ReadUInt64(_fileLength - IndexSectionCount - IndexSectionComponentsCount);

        // <starting block> + <blocks count> * <components count> * 8 + <components count> + <count>
        _indexLength = (
            IndexSectionStartBlock 
            + (int)_blockCount * ((int)_componentsCount * IndexOffsetSize) 
            + IndexSectionComponentsCount 
            + IndexSectionCount
        );

        // Verify that its a block index
        _ = ReadEntry(_fileLength - _indexLength - HeaderSize, EntryTypes.BlockIndex);

        _startBlock = (long?)ReadUInt64(_fileLength - _indexLength);
    }

    public override long AccumulatorOffset
    {
        get
        {
            EnsureIndexAvailable();

            // <index header> + <starting block> + <offset> * 8 + <count>
            long indexLengthIncludingHeader = HeaderSize + _indexLength;

            // <header> + <the 32 byte hash> + <indexes>
            long accumulatorFromLast = E2StoreWriter.HeaderSize + 32 + indexLengthIncludingHeader;

            return _fileLength - accumulatorFromLast;
        }
    }
}
