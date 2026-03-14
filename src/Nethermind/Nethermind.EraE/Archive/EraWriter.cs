// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Era1;
using Nethermind.EraE.E2Store;
using Nethermind.EraE.Proofs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Timestamper = Nethermind.Core.Timestamper;

namespace Nethermind.EraE.Archive;

/// <summary>
/// Writes EraE (.erae) archive files using the section-ordered layout:
///   Version
///   CompressedHeader[0..N-1]
///   CompressedBody[0..N-1]
///   CompressedSlimReceipts[0..N-1]   -- bloom filter stripped per EraE spec
///   TotalDifficulty[0..N-1]          -- pre-merge and transition epochs only
///   AccumulatorRoot                  -- pre-merge and transition epochs only
///   ComponentIndex
///
/// Pre-merge epochs have component-count=4 (header, body, receipts, td).
/// Post-merge epochs have component-count=3 (header, body, receipts).
/// Transition epochs use component-count=4; post-merge blocks use TTD as TotalDifficulty.
/// </summary>
public sealed class EraWriter : IDisposable
{
    public const int MaxEraSize = 8192;

    private readonly HeaderDecoder _headerDecoder = new();
    private readonly BlockBodyDecoder _blockBodyDecoder = new();
    private readonly ReceiptMessageDecoder _slimReceiptDecoder = new(skipBloom: true);
    private readonly E2StoreWriter _e2StoreWriter;
    private readonly ISpecProvider _specProvider;
    private readonly IBeaconRootsProvider? _beaconRootsProvider;
    private readonly SlotTime? _slotTime;

    private readonly ArrayPoolList<(byte[] Buffer, int Length)> _encodedHeaders = new(MaxEraSize);
    private readonly ArrayPoolList<(byte[] Buffer, int Length)> _encodedBodies = new(MaxEraSize);
    private readonly ArrayPoolList<(byte[] Buffer, int Length)> _encodedSlimReceipts = new(MaxEraSize);

    private readonly ArrayPoolList<UInt256> _totalDifficulties = new(MaxEraSize);
    private readonly ArrayPoolList<Hash256> _blockHashes = new(MaxEraSize);

    private long _startNumber;
    private bool _firstBlock = true;
    private bool _finalized;
    private int _preMergeBlockCount;
    private bool _hasPostMergeBlocks;
    private UInt256 _lastPreMergeTD;
    private BlocksRootContext? _blocksRootContext;

    public EraWriter(string path, ISpecProvider specProvider, IBeaconRootsProvider? beaconRootsProvider = null)
        : this(new E2StoreWriter(new FileStream(path, FileMode.Create)), specProvider, beaconRootsProvider)
    {
    }

    public EraWriter(Stream outputStream, ISpecProvider specProvider, IBeaconRootsProvider? beaconRootsProvider = null)
        : this(new E2StoreWriter(outputStream), specProvider, beaconRootsProvider)
    {
    }

    private EraWriter(E2StoreWriter e2StoreWriter, ISpecProvider specProvider, IBeaconRootsProvider? beaconRootsProvider)
    {
        _e2StoreWriter = e2StoreWriter;
        _specProvider = specProvider;
        _beaconRootsProvider = beaconRootsProvider;

        if (beaconRootsProvider is not null && specProvider.BeaconChainGenesisTimestamp.HasValue)
        {
            _slotTime = new SlotTime(
                specProvider.BeaconChainGenesisTimestamp.Value * 1000,
                new Timestamper(),
                TimeSpan.FromSeconds(12),
                TimeSpan.FromSeconds(0));
        }
    }

    public async Task Add(Block block, TxReceipt[] receipts, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(receipts);
        if (_finalized)
            throw new EraException($"Finalize() has been called; no more blocks can be added.");
        if (block.Header is null)
            throw new ArgumentException("Block must have a header.", nameof(block));
        if (block.Hash is null)
            throw new ArgumentException("Block must have a hash.", nameof(block));
        if (_encodedHeaders.Count >= MaxEraSize)
            throw new ArgumentException($"Era file cannot contain more than {MaxEraSize} blocks.");

        if (_firstBlock)
        {
            _startNumber = block.Number;
            _blocksRootContext = new BlocksRootContext(block.Number, block.Header.Timestamp);
            _firstBlock = false;
        }
        else if (block.Number != _startNumber + _encodedHeaders.Count)
        {
            throw new ArgumentException(
                $"Blocks must be added in sequential order. Expected block {_startNumber + _encodedHeaders.Count}, got {block.Number}.",
                nameof(block));
        }

        IReleaseSpec spec = _specProvider.GetSpec(block.Header);
        bool isPostMerge = block.Header.IsPostMerge;

        if (!isPostMerge)
        {
            if (block.TotalDifficulty is null)
                throw new ArgumentException("Pre-merge block must have TotalDifficulty set.", nameof(block));
            if (block.TotalDifficulty < block.Difficulty)
                throw new ArgumentOutOfRangeException(nameof(block.TotalDifficulty), "Cannot be less than block difficulty.");
        }

        RlpBehaviors rlpBehaviors = spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;

        using (NettyRlpStream headerRlp = _headerDecoder.EncodeToNewNettyStream(block.Header, rlpBehaviors))
            _encodedHeaders.Add(RentAndCopy(headerRlp.AsMemory()));

        using (NettyRlpStream bodyRlp = _blockBodyDecoder.EncodeToNewNettyStream(block.Body, rlpBehaviors))
            _encodedBodies.Add(RentAndCopy(bodyRlp.AsMemory()));

        using (NettyRlpStream receiptsRlp = _slimReceiptDecoder.EncodeToNewNettyStream(receipts, rlpBehaviors))
            _encodedSlimReceipts.Add(RentAndCopy(receiptsRlp.AsMemory()));

        _blockHashes.Add(block.Hash);

        if (isPostMerge && _beaconRootsProvider is not null && _slotTime is not null)
        {
            long slot = (long)_slotTime.GetSlot(block.Header.Timestamp * 1000);
            (ValueHash256 beaconBlockRoot, ValueHash256 stateRoot)? roots =
                await _beaconRootsProvider.GetBeaconRoots(slot, cancellation);
            _blocksRootContext!.ProcessBlock(block, roots?.beaconBlockRoot, roots?.stateRoot);
        }
        else
        {
            _blocksRootContext!.ProcessBlock(block);
        }

        if (!isPostMerge)
        {
            _totalDifficulties.Add(block.TotalDifficulty!.Value);
            _lastPreMergeTD = block.TotalDifficulty.Value;
            _preMergeBlockCount++;
        }
        else
        {
            // Placeholder; replaced with TTD in Finalize for transition epochs
            _totalDifficulties.Add(UInt256.Zero);
            _hasPostMergeBlocks = true;
        }

    }

    public async Task<(ValueHash256 AccumulatorRoot, ValueHash256 Checksum)> Finalize(CancellationToken cancellation = default)
    {
        if (_firstBlock)
            throw new EraException("No blocks have been added.");
        if (_finalized)
            throw new EraException("Finalize has already been called.");

        _blocksRootContext!.FinalizeContext();

        bool isTransitionEpoch = _preMergeBlockCount > 0 && _hasPostMergeBlocks;
        bool needsTd = _preMergeBlockCount > 0; // pre-merge or transition
        int componentCount = needsTd ? 4 : 3;
        int blockCount = _encodedHeaders.Count;

        // For transition epochs, fill TTD for all post-merge blocks
        if (isTransitionEpoch)
        {
            for (int i = _preMergeBlockCount; i < blockCount; i++)
                _totalDifficulties[i] = _lastPreMergeTD;
        }

        long totalWritten = 0;
        long[] headerOffsets = ArrayPool<long>.Shared.Rent(blockCount);
        long[] bodyOffsets = ArrayPool<long>.Shared.Rent(blockCount);
        long[] receiptsOffsets = ArrayPool<long>.Shared.Rent(blockCount);
        long[] tdOffsets = needsTd ? ArrayPool<long>.Shared.Rent(blockCount) : [];
        try
        {

        // Version
        totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.Version, Array.Empty<byte>(), cancellation);

        // All CompressedHeaders
        for (int i = 0; i < blockCount; i++)
        {
            headerOffsets[i] = totalWritten;
            (byte[] buf, int len) = _encodedHeaders[i];
            totalWritten += await _e2StoreWriter.WriteEntryAsSnappy(EntryTypes.CompressedHeader, buf.AsMemory(0, len), cancellation);
        }

        // All CompressedBodies
        for (int i = 0; i < blockCount; i++)
        {
            bodyOffsets[i] = totalWritten;
            (byte[] buf, int len) = _encodedBodies[i];
            totalWritten += await _e2StoreWriter.WriteEntryAsSnappy(EntryTypes.CompressedBody, buf.AsMemory(0, len), cancellation);
        }

        // All CompressedSlimReceipts
        for (int i = 0; i < blockCount; i++)
        {
            receiptsOffsets[i] = totalWritten;
            (byte[] buf, int len) = _encodedSlimReceipts[i];
            totalWritten += await _e2StoreWriter.WriteEntryAsSnappy(EntryTypes.CompressedSlimReceipts, buf.AsMemory(0, len), cancellation);
        }

        // All Proof entries for pre-merge blocks (section-ordered, before TotalDifficulty).
        // Post-merge proofs (HistoricalRoots / HistoricalSummaries) require beacon roots and are deferred.
        if (_preMergeBlockCount > 0)
        {
            ProofDecoder proofDecoder = new();
            for (int i = 0; i < _preMergeBlockCount; i++)
            {
                ValueHash256[] proofPath = _blocksRootContext!.GetProof(i);
                BlockHeaderProof proof = new(proofPath);
                byte[] rlpBytes = proofDecoder.Encode(proof).Bytes;
                totalWritten += await _e2StoreWriter.WriteEntryAsSnappy(EntryTypes.Proof, rlpBytes, cancellation);
            }
        }

        // All TotalDifficulty entries (pre-merge and transition epochs)
        if (needsTd)
        {
            for (int i = 0; i < blockCount; i++)
            {
                tdOffsets[i] = totalWritten;
                totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.TotalDifficulty, _totalDifficulties[i].ToLittleEndian(), cancellation);
            }
        }

        // AccumulatorRoot (SSZ hash_tree_root of pre-merge blocks only)
        ValueHash256 accumulatorRoot = default;
        if (needsTd)
        {
            accumulatorRoot = _blocksRootContext!.AccumulatorRoot;
            totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.AccumulatorRoot, accumulatorRoot.ToByteArray(), cancellation);
        }

        // ComponentIndex
        // Layout: starting_number | [header_off, body_off, receipts_off, [td_off]] * N | component_count | block_count
        // Offsets are negative int64 LE, relative to start of the ComponentIndex TLV (including 8-byte header).
        long componentIndexStart = totalWritten; // absolute position of the ComponentIndex entry header
        int indexDataLength = 8 + blockCount * componentCount * 8 + 8 + 8;

        using ArrayPoolList<byte> indexBytes = new(indexDataLength, indexDataLength);
        Span<byte> span = indexBytes.AsSpan();

        WriteInt64(span, 0, _startNumber);

        for (int i = 0; i < blockCount; i++)
        {
            int baseOff = 8 + i * componentCount * 8;
            WriteInt64(span, baseOff + 0, headerOffsets[i] - componentIndexStart);
            WriteInt64(span, baseOff + 8, bodyOffsets[i] - componentIndexStart);
            WriteInt64(span, baseOff + 16, receiptsOffsets[i] - componentIndexStart);
            if (needsTd)
                WriteInt64(span, baseOff + 24, tdOffsets[i] - componentIndexStart);
        }

        int tailOff = 8 + blockCount * componentCount * 8;
        WriteInt64(span, tailOff, componentCount);
        WriteInt64(span, tailOff + 8, blockCount);

        await _e2StoreWriter.WriteEntry(EntryTypes.ComponentIndex, indexBytes.AsMemory(), cancellation);
        await _e2StoreWriter.Flush(cancellation);

        _finalized = true;
        return (accumulatorRoot, _e2StoreWriter.FinalizeChecksum());
        }
        finally
        {
            ArrayPool<long>.Shared.Return(headerOffsets);
            ArrayPool<long>.Shared.Return(bodyOffsets);
            ArrayPool<long>.Shared.Return(receiptsOffsets);
            if (needsTd) ArrayPool<long>.Shared.Return(tdOffsets);
        }
    }

    private static void WriteInt64(Span<byte> destination, int off, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(off, 8), value);

    private static (byte[] Buffer, int Length) RentAndCopy(ReadOnlyMemory<byte> source)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(source.Length);
        source.Span.CopyTo(rented);
        return (rented, source.Length);
    }

    private static void ReturnBuffers(ArrayPoolList<(byte[] Buffer, int Length)> list)
    {
        foreach ((byte[] buf, _) in list.AsSpan())
            ArrayPool<byte>.Shared.Return(buf);
        list.Dispose();
    }

    public void Dispose()
    {
        _blocksRootContext?.Dispose();
        _e2StoreWriter?.Dispose();
        ReturnBuffers(_encodedHeaders);
        ReturnBuffers(_encodedBodies);
        ReturnBuffers(_encodedSlimReceipts);
        _totalDifficulties.Dispose();
        _blockHashes.Dispose();
    }
}
