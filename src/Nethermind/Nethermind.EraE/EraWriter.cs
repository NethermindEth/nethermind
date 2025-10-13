// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.EraE;
public class EraWriter : Era1.EraWriter
{
    private long _startNumber;
    private bool _firstBlock = true;
    private long _totalWritten;
    private readonly ArrayPoolList<long> _entryIndexes;
    private readonly ArrayPoolList<long> _blockHeaderEntryIndexes;
    private readonly ArrayPoolList<long> _blockBodyEntryIndexes;
    private readonly ArrayPoolList<long> _blockReceiptsEntryIndexes;
    private readonly ArrayPoolList<long> _blockTotalDifficultyEntryIndexes;

    private long _blockHeaderTotalWritten;
    private long _blockBodyTotalWritten;
    private long _blockReceiptsTotalWritten;
    private long _blockTotalDifficultyTotalWritten;

    private readonly HeaderDecoder _headerDecoder = new();
    private readonly BlockBodyDecoder _blockBodyDecoder = new();
    private readonly ReceiptMessageDecoder _receiptDecoder = new(skipBloom: true);

    private readonly Era1.E2StoreWriter _e2StoreWriter;
    private readonly AccumulatorCalculator _accumulatorCalculator;
    private readonly ISpecProvider _specProvider;
    private bool _finalized;

    private readonly Era1.E2StoreWriter _blockHeaderE2StoreWriter;
    private readonly Era1.E2StoreWriter _blockBodyE2StoreWriter;
    private readonly Era1.E2StoreWriter _blockReceiptsE2StoreWriter;
    private readonly Era1.E2StoreWriter _blockTotalDifficultyE2StoreWriter;

    public EraWriter(string path, ISpecProvider specProvider)
        : this(new Era1.E2StoreWriter(new FileStream(path, FileMode.Create)), specProvider)
    {
    }

    public EraWriter(Stream outputStream, ISpecProvider specProvider)
        : this(new Era1.E2StoreWriter(outputStream), specProvider)
    {
    }

    private EraWriter(Era1.E2StoreWriter e2StoreWriter, ISpecProvider specProvider): base(e2StoreWriter, specProvider)
    {
        // get temp directory
        string tempDir = Path.Combine(Path.GetTempPath(), "erae_temp");
        Directory.CreateDirectory(tempDir);

        _blockHeaderE2StoreWriter = new Era1.E2StoreWriter(new FileStream(Path.Combine(tempDir, "block_header.e2store"), FileMode.Create));
        _blockBodyE2StoreWriter = new Era1.E2StoreWriter(new FileStream(Path.Combine(tempDir, "block_body.e2store"), FileMode.Create));
        _blockReceiptsE2StoreWriter = new Era1.E2StoreWriter(new FileStream(Path.Combine(tempDir, "block_receipts.e2store"), FileMode.Create));
        _blockTotalDifficultyE2StoreWriter = new Era1.E2StoreWriter(new FileStream(Path.Combine(tempDir, "block_total_difficulty.e2store"), FileMode.Create));
    }

    public async Task Add(Block block, TxReceipt[] receipts, CancellationToken cancellation = default)
    {
        // write header, body, receipts, total difficulty to the separate files
        // which are the e2store format files and then combine ones along with the componentsIndex to an output file
        // at Finalize() method.
        if (_finalized)
            throw new Era1.EraException($"Finalized() has been called on this {nameof(EraWriter)}, and no more blocks can be added. ");

        if (block.Header is null)
            throw new ArgumentException("The block must have a header.", nameof(block));
        if (block.Hash is null)
            throw new ArgumentException("The block must have a hash.", nameof(block));

        if (_entryIndexes.Count >= MaxEra1Size)
            throw new ArgumentException($"Era file should not contain more than {MaxEra1Size} blocks");

        if (_firstBlock)
        {
            _startNumber = block.Number;
            _totalWritten += await WriteVersion();
            _firstBlock = false;
        }

        if (block.TotalDifficulty < block.Difficulty)
            throw new ArgumentOutOfRangeException(nameof(block.TotalDifficulty), $"Cannot be less than the block difficulty.");

        _blockHeaderEntryIndexes.Add(_blockHeaderTotalWritten);
        _blockBodyEntryIndexes.Add(_blockBodyTotalWritten);
        _blockReceiptsEntryIndexes.Add(_blockReceiptsTotalWritten);
        _blockTotalDifficultyEntryIndexes.Add(_blockTotalDifficultyTotalWritten);
        
        _accumulatorCalculator.Add(block.Hash, block.TotalDifficulty!.Value);


        RlpBehaviors behaviors = _specProvider.GetSpec(block.Header).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;

        using (NettyRlpStream headerBytes = _headerDecoder.EncodeToNewNettyStream(block.Header, behaviors))
        {
            _blockHeaderTotalWritten += await _blockHeaderE2StoreWriter.WriteEntryAsSnappy(EntryTypes.CompressedHeader, headerBytes.AsMemory(), cancellation);
        }

        using (NettyRlpStream bodyBytes = _blockBodyDecoder.EncodeToNewNettyStream(block.Body, behaviors))
        {
            _blockBodyTotalWritten += await _blockBodyE2StoreWriter.WriteEntryAsSnappy(EntryTypes.CompressedBody, bodyBytes.AsMemory(), cancellation);
        }

        using (NettyRlpStream receiptBytes = _receiptDecoder.EncodeToNewNettyStream(receipts, behaviors))
        {
            _blockReceiptsTotalWritten += await _blockReceiptsE2StoreWriter.WriteEntryAsSnappy(EntryTypes.CompressedSlimReceipts, receiptBytes.AsMemory(), cancellation);
        }
        
        _blockTotalDifficultyTotalWritten += await _blockTotalDifficultyE2StoreWriter.WriteEntry(EntryTypes.TotalDifficulty, block.TotalDifficulty!.Value.ToLittleEndian(), cancellation);
    }

    public async Task<(ValueHash256, ValueHash256)> Finalize(CancellationToken cancellation = default)
    {
        if (_firstBlock)
            throw new EraException("Finalize was called, but no blocks have been added yet.");

        int componentsCount = 4;

        _totalWritten += await WriteVersion();

        long blockIndexPosition = _totalWritten + _blockHeaderTotalWritten + _blockBodyTotalWritten + _blockReceiptsTotalWritten + _blockTotalDifficultyTotalWritten;

        //24 bytes is for the start, components-count and block-count plus every entry. Every entry is 8 bytes times the components count.
        int length = 24 + _blockHeaderEntryIndexes.Count * componentsCount * 8;
        using var componentsIndex = new ArrayPoolList<byte>(length, length);
        Span<byte> componentsIndexSpan = componentsIndex.AsSpan();
        // write the start number at the beginning (i.e. first 8 bytes)
        WriteInt64(componentsIndexSpan, 0, _startNumber);

        for (int i = 0; i < _blockHeaderEntryIndexes.Count; i++)
        {
            //Skip 8 bytes for the start value
            // TODO: recalculate the _blockHeaderEntryIndexes[i] position, because it's offsets are in a separate file, but we need to
            // account for the Version entry.
            WriteInt64(componentsIndexSpan, 1 * 8 + i * 8, _totalWritten + _blockHeaderEntryIndexes[i] - blockIndexPosition);
            WriteInt64(componentsIndexSpan, 2 * 8 + i * 8, _totalWritten + _blockHeaderTotalWritten + _blockBodyEntryIndexes[i] - blockIndexPosition);
            WriteInt64(componentsIndexSpan, 3 * 8 + i * 8, _totalWritten + _blockHeaderTotalWritten + _blockBodyTotalWritten +  _blockReceiptsEntryIndexes[i] - blockIndexPosition);
            WriteInt64(componentsIndexSpan, 4 * 8 + i * 8, _totalWritten + _blockHeaderTotalWritten + _blockBodyTotalWritten + _blockReceiptsTotalWritten + _blockTotalDifficultyEntryIndexes[i] - blockIndexPosition);
        }

        WriteInt64(componentsIndexSpan, 8 + _blockHeaderEntryIndexes.Count * componentsCount * 8, componentsCount);
        WriteInt64(componentsIndexSpan, 16 + _blockHeaderEntryIndexes.Count * componentsCount * 8, _blockHeaderEntryIndexes.Count);

        await _e2StoreWriter.WriteEntry(EntryTypes.BlockIndex, componentsIndex.AsMemory(), cancellation);
        await _e2StoreWriter.Flush(cancellation);

        _entryIndexes.Clear();
        _accumulatorCalculator.Clear();
        _finalized = true;


        // recalculate the entry indexes for the block header, body, receipts, total difficulty.


        ValueHash256 root = _accumulatorCalculator.ComputeRoot();
        _totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.Accumulator, root.ToByteArray(), cancellation);

        //Index is 64 bits segments in the format => start | index | index | ... | count
        //16 bytes is for the start and count plus every entry
        // int length = 16 + _entryIndexes.Count * 8;
        using ArrayPoolList<byte> blockIndex = new ArrayPoolList<byte>(length, length);
        Span<byte> blockIndexSpan = blockIndex.AsSpan();
        WriteInt64(blockIndexSpan, 0, _startNumber);

        //era1:= Version | block-tuple ... | other-entries ... | Accumulator | BlockIndex
        //block-index := starting-number | index | index | index... | count

        //All positions are relative to the end position in the index
        for (int i = 0; i < _entryIndexes.Count; i++)
        {
            //Skip 8 bytes for the start value
            WriteInt64(blockIndexSpan, 8 + i * 8, _entryIndexes[i] - blockIndexPosition);
        }

        WriteInt64(blockIndexSpan, 8 + _entryIndexes.Count * 8, _entryIndexes.Count);

        await _e2StoreWriter.WriteEntry(EntryTypes.BlockIndex, blockIndex.AsMemory(), cancellation);
        await _e2StoreWriter.Flush(cancellation);

        _entryIndexes.Clear();
        _accumulatorCalculator.Clear();
        _finalized = true;
        return (root, _e2StoreWriter.FinalizeChecksum());
    }

    private static void WriteInt64(Span<byte> destination, int off, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(off, 8), value);
    }

    private Task<int> WriteVersion()
    {
        return _e2StoreWriter.WriteEntry(EntryTypes.Version, Array.Empty<byte>());
    }

}
