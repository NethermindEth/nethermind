// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.EraE;

/// <summary>
/// Main reader for erae file. Uses E2StoreReader which internally mmap the whole file. This reader is thread safe
/// allowing multiple thread to read from it at the same time.
/// </summary>

// set skipBloom to true, because EraE format omits that field in archive and we need to compute filter locally,
// which is handled in an appropriate class later.
public class EraReader(E2StoreReader e2) : Era1.EraReader(e2, new ReceiptMessageDecoder(skipBloom: true))
{

    protected override async Task<EntryReadResult> ReadBlockAndReceipts(long blockNumber, bool computeHeaderHash, CancellationToken cancellationToken)
    {
        if (blockNumber < _fileReader.First
            || blockNumber > _fileReader.First + _fileReader.BlockCount)
            throw new ArgumentOutOfRangeException("Value is outside the range of the archive.", blockNumber, nameof(blockNumber));
        // cast to E2StoreReader to access the BlockOffset method which is overridden in EraE
        (long headerPosition, long bodyPosition, long receiptsPosition, long? tdPositionOptional) = ((E2StoreReader)_fileReader).BlockOffset(blockNumber);

        (BlockHeader header, long _) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            headerPosition,
            DecodeHeader,
            EntryTypes.CompressedHeader,
            cancellationToken);

        (BlockBody body, long _) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            bodyPosition,
            DecodeBody,
            EntryTypes.CompressedBody, 
            cancellationToken);

        (TxReceipt[] receipts, long _) = await _fileReader.ReadSnappyCompressedEntryAndDecode(
            receiptsPosition,
            DecodeReceipts,
            EntryTypes.CompressedSlimReceipts, 
            cancellationToken);

        // if total difficulty is available for a specific block, decode one.
        if (tdPositionOptional is { } tdPosition) {
            _ = _fileReader.ReadEntryAndDecode(
                tdPosition,
                static (buffer) => new UInt256(buffer.Span, isBigEndian: false),
                EntryTypes.TotalDifficulty,
                out UInt256 currentTotalDiffulty);
            header.TotalDifficulty = currentTotalDiffulty;
        }

        var block = new Block(header, body);
        return new EntryReadResult(block, receipts);
    }
}
