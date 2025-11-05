// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.EraE;

public struct ComponentType {
    public const int Header = 0;
    public const int Body = 1;
    public const int Receipts = 2;
    public const int Proof = 3;
    public const int TotalDifficulty = 4;
}


public class EraWriter : Era1.EraWriter
{
    public const int MaxEraESize = 8192;

    private readonly ArrayPoolList<long>[] _componentEntryIndexes;
    private readonly long[] _totalWrittenPerComponent;

    private readonly FileStream _blockHeaderStream;
    private readonly FileStream _blockBodyStream;
    private readonly FileStream _blockReceiptsStream;
    private readonly FileStream _blockTotalDifficultyStream;

    private readonly E2StoreWriter _blockHeaderE2StoreWriter;
    private readonly E2StoreWriter _blockBodyE2StoreWriter;
    private readonly E2StoreWriter _blockReceiptsE2StoreWriter;
    private readonly E2StoreWriter _blockTotalDifficultyE2StoreWriter;

    private BlocksRootContext? _blocksRootContext;

    private bool _isPostMerge;

    // it's 3 for post-merge because we don't write TotalDifficulty field and Proof fields
    // and it's 4 for pre-merge because we write TotalDifficulty field but not Proof field
    private int ComponentsCount => _isPostMerge ? 4 : 3;

    public EraWriter(string path, ISpecProvider specProvider)
        : this(new FileStream(path, FileMode.Create), specProvider)
    {
    }

    public EraWriter(Stream outputStream, ISpecProvider specProvider)
        : base(new E2StoreWriter(outputStream), specProvider, new ReceiptMessageDecoder(skipBloom: true))
    {
        // get temp directory
        string tempDir = Path.Combine(Path.GetTempPath(), "erae_temp");
        Directory.CreateDirectory(tempDir);

        _blockHeaderStream = new FileStream(Path.Combine(tempDir, "block_header.e2store"), FileMode.Create);
        _blockBodyStream = new FileStream(Path.Combine(tempDir, "block_body.e2store"), FileMode.Create);
        _blockReceiptsStream = new FileStream(Path.Combine(tempDir, "block_receipts.e2store"), FileMode.Create);
        _blockTotalDifficultyStream = new FileStream(Path.Combine(tempDir, "block_total_difficulty.e2store"), FileMode.Create);

        _blockHeaderE2StoreWriter = new E2StoreWriter(_blockHeaderStream);
        _blockBodyE2StoreWriter = new E2StoreWriter(_blockBodyStream);
        _blockReceiptsE2StoreWriter = new E2StoreWriter(_blockReceiptsStream);
        _blockTotalDifficultyE2StoreWriter = new E2StoreWriter(_blockTotalDifficultyStream);

        // we still initialize the array for all 5 components later to use ComponentType enum for indexing
        _componentEntryIndexes = new ArrayPoolList<long>[5];
        _totalWrittenPerComponent = new long[5];

        // initialize the arrays
        _componentEntryIndexes[ComponentType.Header] = new ArrayPoolList<long>(MaxEraESize);
        _componentEntryIndexes[ComponentType.Body] = new ArrayPoolList<long>(MaxEraESize);
        _componentEntryIndexes[ComponentType.Receipts] = new ArrayPoolList<long>(MaxEraESize);
        _componentEntryIndexes[ComponentType.TotalDifficulty] = new ArrayPoolList<long>(MaxEraESize);
    }

    private async Task AddPreMergeBlock(Block block, CancellationToken cancellation = default) {
        if (block.TotalDifficulty < block.Difficulty)
            throw new ArgumentOutOfRangeException(nameof(block.TotalDifficulty), $"Cannot be less than the block difficulty.");

        _componentEntryIndexes[ComponentType.TotalDifficulty].Add(_totalWrittenPerComponent[ComponentType.TotalDifficulty]);

        _totalWrittenPerComponent[ComponentType.TotalDifficulty] += await _blockTotalDifficultyE2StoreWriter.WriteEntry(EntryTypes.TotalDifficulty, block.TotalDifficulty!.Value.ToLittleEndian(), cancellation);
    }

    public override async Task Add(Block block, TxReceipt[] receipts, CancellationToken cancellation = default)
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

        if (_componentEntryIndexes[ComponentType.Header].Count >= MaxEraESize)
            throw new ArgumentException($"Era file should not contain more than {MaxEraESize} blocks");

        if (_firstBlock)
        {
            _blocksRootContext = new BlocksRootContext(block.Number, block.Header.Timestamp);
            _startNumber = block.Number;
            _totalWritten += await WriteVersion();
            _firstBlock = false;
            _isPostMerge = block.IsPoS();
        }

        _blocksRootContext!.ProcessBlock(block);

        if (!_isPostMerge) {
            await AddPreMergeBlock(block, cancellation);
        }

        _componentEntryIndexes[ComponentType.Header].Add(_totalWrittenPerComponent[ComponentType.Header]);
        _componentEntryIndexes[ComponentType.Body].Add(_totalWrittenPerComponent[ComponentType.Body]);
        _componentEntryIndexes[ComponentType.Receipts].Add(_totalWrittenPerComponent[ComponentType.Receipts]);

        RlpBehaviors behaviors = _specProvider.GetSpec(block.Header).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;

        using (NettyRlpStream headerBytes = _headerDecoder.EncodeToNewNettyStream(block.Header, behaviors))
        {
            _totalWrittenPerComponent[ComponentType.Header] += await _blockHeaderE2StoreWriter.WriteEntryAsSnappy(EntryTypes.CompressedHeader, headerBytes.AsMemory(), cancellation);
        }

        using (NettyRlpStream bodyBytes = _blockBodyDecoder.EncodeToNewNettyStream(block.Body, behaviors))
        {
            _totalWrittenPerComponent[ComponentType.Body] += await _blockBodyE2StoreWriter.WriteEntryAsSnappy(EntryTypes.CompressedBody, bodyBytes.AsMemory(), cancellation);
        }

        using (NettyRlpStream receiptBytes = _receiptDecoder.EncodeToNewNettyStream(receipts, behaviors))
        {
            _totalWrittenPerComponent[ComponentType.Receipts] += await _blockReceiptsE2StoreWriter.WriteEntryAsSnappy(EntryTypes.CompressedSlimReceipts, receiptBytes.AsMemory(), cancellation);
        }
    }

    public new async Task<ValueHash256> Finalize(CancellationToken cancellation = default)
    {
        // array of component offsets in file
        long[] componentOffsetsInFile = new long[5];

        if (_firstBlock)
            throw new Era1.EraException("Finalize was called, but no blocks have been added yet.");

        _blocksRootContext!.FinalizeContext();

        var blocksCount = _componentEntryIndexes[ComponentType.Header].Count;

        _totalWritten += await WriteVersion();
        // write contents of the separate files to the main file
        componentOffsetsInFile[ComponentType.Header] = _totalWritten;
        _totalWritten += await (_e2StoreWriter as E2StoreWriter)!.WriteEntriesFromRawStream(_blockHeaderStream, cancellation);
        componentOffsetsInFile[ComponentType.Body] = _totalWritten;
        _totalWritten += await (_e2StoreWriter as E2StoreWriter)!.WriteEntriesFromRawStream(_blockBodyStream, cancellation);
        componentOffsetsInFile[ComponentType.Receipts] = _totalWritten;
        _totalWritten += await (_e2StoreWriter as E2StoreWriter)!.WriteEntriesFromRawStream(_blockReceiptsStream, cancellation);


        if (_isPostMerge) {
            componentOffsetsInFile[ComponentType.TotalDifficulty] = _totalWritten;
            _totalWritten += await (_e2StoreWriter as E2StoreWriter)!.WriteEntriesFromRawStream(_blockTotalDifficultyStream, cancellation);

            ValueHash256 root = _blocksRootContext.AccumulatorRoot;
            _totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.Accumulator, root.ToByteArray(), cancellation);
        }

        long blockIndexPosition = _totalWritten;
        //24 bytes is for the start, components-count and block-count plus every entry. Every entry is 8 bytes times the components count.
        int length = 24 + blocksCount * ComponentsCount * 8;
        using var componentsIndex = new ArrayPoolList<byte>(length, length);
        Span<byte> componentsIndexSpan = componentsIndex.AsSpan();
        // write the start number at the beginning (i.e. first 8 bytes)
        WriteInt64(componentsIndexSpan, 0, _startNumber);

        for (int i = 0; i < blocksCount; i++)
        {
            {
                // we skip 8 bytes for the start value, for second component, we skip 8 bytes for the start value and 8 bytes for the first component, etc.
                // We also need to recalculate offsets for each component since we are writing them to separate files and their indexes in componentOffsetsInFile will start from 0 but in the final file
                // they need to start from the offset of the component itself.
                WriteInt64(componentsIndexSpan, 8, (_componentEntryIndexes[ComponentType.Header][i] + componentOffsetsInFile[ComponentType.Header] - blockIndexPosition));
                WriteInt64(componentsIndexSpan, 16, (_componentEntryIndexes[ComponentType.Body][i] + componentOffsetsInFile[ComponentType.Body] - blockIndexPosition));
                WriteInt64(componentsIndexSpan, 24, (_componentEntryIndexes[ComponentType.Receipts][i] + componentOffsetsInFile[ComponentType.Receipts] - blockIndexPosition));

                if (!_isPostMerge) {
                    WriteInt64(componentsIndexSpan, 32, (_componentEntryIndexes[ComponentType.TotalDifficulty][i] + componentOffsetsInFile[ComponentType.TotalDifficulty] - blockIndexPosition));
                }

            }
        }

        WriteInt64(componentsIndexSpan, 8 + blocksCount * ComponentsCount * 8, ComponentsCount);
        WriteInt64(componentsIndexSpan, 16 + blocksCount * ComponentsCount * 8, blocksCount);

        await _e2StoreWriter.WriteEntry(EntryTypes.BlockIndex, componentsIndex.AsMemory(), cancellation);
        await _e2StoreWriter.Flush(cancellation);

        _componentEntryIndexes[ComponentType.Header].Clear();
        _componentEntryIndexes[ComponentType.Body].Clear();
        _componentEntryIndexes[ComponentType.Receipts].Clear();

        if (ComponentsCount > 3)
            _componentEntryIndexes[ComponentType.TotalDifficulty].Clear();
        if (ComponentsCount == 5)
            _componentEntryIndexes[ComponentType.Proof].Clear();

        _accumulatorCalculator.Clear();
        _finalized = true;
        return _e2StoreWriter.FinalizeChecksum();
    }
}
