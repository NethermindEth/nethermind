// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using Microsoft.IO;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Era1;
using Nethermind.EraE.E2Store;
using Nethermind.EraE.Proofs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Snappier;
using Timestamper = Nethermind.Core.Timestamper;

namespace Nethermind.EraE.Archive;

/// <summary>
/// Writes an EraE archive file.
/// Each call to <see cref="Add"/> immediately compresses the block's header, body, and receipts
/// via Snappy and writes them directly to the output stream. This keeps peak memory per epoch
/// proportional to a single block's data rather than the full epoch, enabling high export
/// concurrency without OOM risk. <see cref="Finalize"/> writes the TotalDifficulty entries,
/// AccumulatorRoot, and ComponentIndex using byte offsets recorded during <see cref="Add"/>.
/// </summary>
public sealed class EraWriter : IDisposable
{
    public const int MaxEraSize = 8192;

    private const long MillisecondsPerSecond = 1000;
    private const int BeaconSlotSeconds = 12;
    private const int IndexFieldSize = 8; // sizeof(long) — each ComponentIndex field is a little-endian int64

    private readonly HeaderDecoder _headerDecoder = new();
    private readonly BlockBodyDecoder _blockBodyDecoder = new();
    private readonly ReceiptMessageDecoder _slimReceiptDecoder = new(skipBloom: true);
    private readonly E2StoreWriter _e2StoreWriter;
    private readonly ISpecProvider _specProvider;
    private readonly IBeaconRootsProvider? _beaconRootsProvider;
    private readonly SlotTime? _slotTime;

    // Per-block byte offsets recorded in Add() and consumed in Finalize() for the ComponentIndex.
    // Each stores the absolute file position of the entry's TLV header.
    private readonly ArrayPoolList<long> _headerOffsets = new(MaxEraSize);
    private readonly ArrayPoolList<long> _bodyOffsets = new(MaxEraSize);
    private readonly ArrayPoolList<long> _receiptsOffsets = new(MaxEraSize);

    private readonly ArrayPoolList<UInt256> _totalDifficulties = new(MaxEraSize);

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
                specProvider.BeaconChainGenesisTimestamp.Value * MillisecondsPerSecond,
                new Timestamper(),
                TimeSpan.FromSeconds(BeaconSlotSeconds),
                TimeSpan.Zero);
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
        if (_headerOffsets.Count >= MaxEraSize)
            throw new ArgumentException($"Era file cannot contain more than {MaxEraSize} blocks.");

        if (_firstBlock)
        {
            _startNumber = block.Number;
            _blocksRootContext = new BlocksRootContext(block.Number, block.Header.Timestamp, _specProvider);
            _firstBlock = false;
            await _e2StoreWriter.WriteEntry(EntryTypes.Version, Array.Empty<byte>(), cancellation);
        }
        else if (block.Number != _startNumber + _headerOffsets.Count)
        {
            throw new ArgumentException(
                $"Blocks must be added in sequential order. Expected block {_startNumber + _headerOffsets.Count}, got {block.Number}.",
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
        {
            _headerOffsets.Add(_e2StoreWriter.Position);
            await WriteCompressed(EntryTypes.CompressedHeader, headerRlp.AsMemory(), cancellation);
        }

        using (NettyRlpStream bodyRlp = _blockBodyDecoder.EncodeToNewNettyStream(block.Body, rlpBehaviors))
        {
            _bodyOffsets.Add(_e2StoreWriter.Position);
            await WriteCompressed(EntryTypes.CompressedBody, bodyRlp.AsMemory(), cancellation);
        }

        using (NettyRlpStream receiptsRlp = _slimReceiptDecoder.EncodeToNewNettyStream(receipts, rlpBehaviors))
        {
            _receiptsOffsets.Add(_e2StoreWriter.Position);
            await WriteCompressed(EntryTypes.CompressedSlimReceipts, receiptsRlp.AsMemory(), cancellation);
        }

        if (isPostMerge && _beaconRootsProvider is not null && _slotTime is not null)
        {
            long slot = (long)_slotTime.GetSlot(block.Header.Timestamp * MillisecondsPerSecond);
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
        bool needsTd = _preMergeBlockCount > 0;
        int componentCount = needsTd ? 4 : 3;
        int blockCount = _headerOffsets.Count;

        if (isTransitionEpoch)
        {
            for (int i = _preMergeBlockCount; i < blockCount; i++)
                _totalDifficulties[i] = _lastPreMergeTD;
        }

        long[] tdOffsets = needsTd ? ArrayPool<long>.Shared.Rent(blockCount) : [];
        try
        {
            if (needsTd)
            {
                for (int i = 0; i < blockCount; i++)
                {
                    tdOffsets[i] = _e2StoreWriter.Position;
                    await _e2StoreWriter.WriteEntry(EntryTypes.TotalDifficulty, _totalDifficulties[i].ToLittleEndian(), cancellation);
                }
            }

            ValueHash256 accumulatorRoot = default;
            if (needsTd)
            {
                accumulatorRoot = _blocksRootContext!.AccumulatorRoot;
                await _e2StoreWriter.WriteEntry(EntryTypes.AccumulatorRoot, accumulatorRoot.ToByteArray(), cancellation);
            }

            // ComponentIndex
            // Layout: starting_number | [header_off, body_off, receipts_off, [td_off]] * N | component_count | block_count
            // Offsets are negative int64 LE, relative to start of the ComponentIndex TLV (including 8-byte header).
            long componentIndexStart = _e2StoreWriter.Position;
            int indexDataLength = IndexFieldSize + blockCount * componentCount * IndexFieldSize + IndexFieldSize + IndexFieldSize;

            using ArrayPoolList<byte> indexBytes = new(indexDataLength, indexDataLength);
            Span<byte> span = indexBytes.AsSpan();

            WriteInt64(span, 0, _startNumber);

            for (int i = 0; i < blockCount; i++)
            {
                int baseOff = IndexFieldSize + i * componentCount * IndexFieldSize;
                WriteInt64(span, baseOff + IndexFieldSize * 0, _headerOffsets[i] - componentIndexStart);
                WriteInt64(span, baseOff + IndexFieldSize * 1, _bodyOffsets[i] - componentIndexStart);
                WriteInt64(span, baseOff + IndexFieldSize * 2, _receiptsOffsets[i] - componentIndexStart);
                if (needsTd)
                    WriteInt64(span, baseOff + IndexFieldSize * 3, tdOffsets[i] - componentIndexStart);
            }

            int tailOff = IndexFieldSize + blockCount * componentCount * IndexFieldSize;
            WriteInt64(span, tailOff, componentCount);
            WriteInt64(span, tailOff + IndexFieldSize, blockCount);

            await _e2StoreWriter.WriteEntry(EntryTypes.ComponentIndex, indexBytes.AsMemory(), cancellation);
            await _e2StoreWriter.Flush(cancellation);

            _finalized = true;
            return (accumulatorRoot, _e2StoreWriter.FinalizeChecksum());
        }
        finally
        {
            if (needsTd) ArrayPool<long>.Shared.Return(tdOffsets);
        }
    }

    public void Dispose()
    {
        _blocksRootContext?.Dispose();
        _e2StoreWriter?.Dispose();
        _headerOffsets.Dispose();
        _bodyOffsets.Dispose();
        _receiptsOffsets.Dispose();
        _totalDifficulties.Dispose();
    }

    private async Task WriteCompressed(ushort entryType, ReadOnlyMemory<byte> data, CancellationToken cancellation)
    {
        using RecyclableMemoryStream ms = RecyclableStream.GetStream(nameof(EraWriter));
        using (SnappyStream compressor = new(ms, CompressionMode.Compress, leaveOpen: true))
        {
            compressor.Write(data.Span);
            compressor.Flush();
        }
        bool ok = ms.TryGetBuffer(out ArraySegment<byte> segment);
        Debug.Assert(ok);
        await _e2StoreWriter.WriteEntry(entryType, segment.AsMemory(), cancellation);
    }

    private static void WriteInt64(Span<byte> destination, int off, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(off, IndexFieldSize), value);
}
