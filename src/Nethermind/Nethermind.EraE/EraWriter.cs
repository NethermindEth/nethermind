// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.Int256;

namespace Nethermind.EraE;

public class EraWriter : Era1.EraWriter
{
    public const int MaxEraESize = 8192;

    private readonly ArrayPoolList<Memory<byte>> _blockHeaders = new(MaxEraESize);
    private readonly ArrayPoolList<Memory<byte>> _blockBodies = new(MaxEraESize);
    private readonly ArrayPoolList<Memory<byte>> _blockReceipts = new(MaxEraESize);
    private readonly ArrayPoolList<UInt256> _blockTotalDifficulties = new(MaxEraESize);

    private BlocksRootContext? _blocksRootContext;

    private bool _isPostMerge;

    public EraWriter(string path, ISpecProvider specProvider)
        : this(new FileStream(path, FileMode.Create), specProvider)
    {
    }

    public EraWriter(Stream outputStream, ISpecProvider specProvider)
        : base(new E2StoreWriter(outputStream), specProvider, new ReceiptMessageDecoder(skipBloom: true))
    {

    }

    private void AddPreMergeBlock(Block block) {
        if (block.TotalDifficulty < block.Difficulty)
            throw new ArgumentOutOfRangeException(nameof(block.TotalDifficulty), $"Cannot be less than the block difficulty.");

        _blockTotalDifficulties.Add(block.TotalDifficulty!.Value);
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

        if (_blockHeaders.Count >= MaxEraESize)
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

        if (!_isPostMerge) AddPreMergeBlock(block);

        RlpBehaviors behaviors = _specProvider.GetSpec(block.Header).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;
        using (NettyRlpStream headerBytes = _headerDecoder.EncodeToNewNettyStream(block.Header, behaviors))
        {
            _blockHeaders.Add(new Memory<byte>(headerBytes.AsMemory().ToArray()));
        }

        using (NettyRlpStream bodyBytes = _blockBodyDecoder.EncodeToNewNettyStream(block.Body, behaviors))
        {
            _blockBodies.Add(new Memory<byte>(bodyBytes.AsMemory().ToArray()));
        }

        using (NettyRlpStream receiptBytes = _receiptDecoder.EncodeToNewNettyStream(receipts, behaviors))
        {
            _blockReceipts.Add(new Memory<byte>(receiptBytes.AsMemory().ToArray()));
        }
    }

    public new async Task<ValueHash256> Finalize(CancellationToken cancellation = default)
    {
        if (_firstBlock)
            throw new Era1.EraException("Finalize was called, but no blocks have been added yet.");

        _blocksRootContext!.FinalizeContext();

        var blocksCount = _blockBodies.Count;

        using ArrayPoolList<long> headerOffsets = new(blocksCount);
        using ArrayPoolList<long> bodyOffsets = new(blocksCount);
        using ArrayPoolList<long> receiptOffsets = new(blocksCount);
        using ArrayPoolList<long> totalDifficultyOffsets = new(blocksCount);

        for (int i = 0; i < blocksCount; i++) {
            headerOffsets.Add(_totalWritten);
            _totalWritten += await _e2StoreWriter.WriteEntryAsSnappy(EntryTypes.CompressedHeader, _blockHeaders[i], cancellation);
        }
        for (int i = 0; i < blocksCount; i++) {
            bodyOffsets.Add(_totalWritten);
            _totalWritten += await _e2StoreWriter.WriteEntryAsSnappy(EntryTypes.CompressedBody, _blockBodies[i], cancellation);
        }
        for (int i = 0; i < blocksCount; i++) {
            receiptOffsets.Add(_totalWritten);
            _totalWritten += await _e2StoreWriter.WriteEntryAsSnappy(EntryTypes.CompressedSlimReceipts, _blockReceipts[i], cancellation);
        }
        if (!_isPostMerge) {
            for (int i = 0; i < blocksCount; i++) {
                totalDifficultyOffsets.Add(_totalWritten);
                _totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.TotalDifficulty, _blockTotalDifficulties[i].ToLittleEndian(), cancellation);
            }
            ValueHash256 root = _blocksRootContext.AccumulatorRoot;
            _totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.Accumulator, root.ToByteArray(), cancellation);
        }

        int componentsCount = _isPostMerge ? 3 : 4;

        long blockIndexPosition = _totalWritten;
        //24 bytes is for the start, components-count and block-count plus every entry. Every entry is 8 bytes times the components count.
        int length = 24 + blocksCount * componentsCount * 8;
        using var componentsIndex = new ArrayPoolList<byte>(length, length);
        Span<byte> componentsIndexSpan = componentsIndex.AsSpan();
        // write the start number at the beginning (i.e. first 8 bytes)
        WriteInt64(componentsIndexSpan, 0, _startNumber);

        for (int i = 0; i < blocksCount; i++)
        {
            // we skip 8 bytes for the start value, for second component, we skip 8 bytes for the start value and 8 bytes for the first component, etc.
            WriteInt64(componentsIndexSpan, i * componentsCount * 8 + 8, headerOffsets[i] - blockIndexPosition);
            WriteInt64(componentsIndexSpan, i * componentsCount * 8 + 16, bodyOffsets[i] - blockIndexPosition);
            WriteInt64(componentsIndexSpan, i * componentsCount * 8 + 24, receiptOffsets[i] - blockIndexPosition);
            if (!_isPostMerge) WriteInt64(componentsIndexSpan, i * componentsCount * 8 + 32, totalDifficultyOffsets[i] - blockIndexPosition);
        }

        WriteInt64(componentsIndexSpan, 8 + blocksCount * componentsCount * 8, componentsCount);
        WriteInt64(componentsIndexSpan, 16 + blocksCount * componentsCount * 8, blocksCount);

        await _e2StoreWriter.WriteEntry(EntryTypes.BlockIndex, componentsIndex.AsMemory(), cancellation);
        await _e2StoreWriter.Flush(cancellation);

        _accumulatorCalculator.Clear();
        _finalized = true;

        return _e2StoreWriter.FinalizeChecksum();
    }
}
