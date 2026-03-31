// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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
/// Writes an EraE archive file.
/// Each call to <see cref="Add"/> compresses the block's header, body, and receipts immediately
/// and writes them to per-component temporary streams, so peak memory per epoch is O(block count)
/// for offset bookkeeping only — not O(block data size).
/// <see cref="Finalize"/> assembles the final file from the temp streams in the required
/// grouped layout (all headers, then all bodies, then all receipts) and appends the index.
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

    // Per-component temp writers. Add() writes compressed entries to these immediately,
    // avoiding in-memory buffering of all block data until Finalize().
    private readonly E2StoreWriter _headersTempWriter;
    private readonly E2StoreWriter _bodiesTempWriter;
    private readonly E2StoreWriter _receiptsTempWriter;

    // Byte offset of each block's entry within its temp stream.
    // These are small (8192 × 8 bytes = 64 KB per array) regardless of block size.
    private readonly long[] _headerOffsets = new long[MaxEraSize];
    private readonly long[] _bodyOffsets = new long[MaxEraSize];
    private readonly long[] _receiptsOffsets = new long[MaxEraSize];

    // Paths of the temp files created on disk; null when using in-memory streams (tests/small eras).
    private readonly string?[]? _tempFilePaths;

    private readonly ArrayPoolList<UInt256> _totalDifficulties = new(MaxEraSize);

    private int _count;
    private long _startNumber;
    private bool _firstBlock = true;
    private bool _finalized;
    private int _preMergeBlockCount;
    private bool _hasPostMergeBlocks;
    private UInt256 _lastPreMergeTD;
    private BlocksRootContext? _blocksRootContext;

    /// <param name="path">Output file path. Temp files are placed in the same directory.</param>
    public EraWriter(string path, ISpecProvider specProvider, IBeaconRootsProvider? beaconRootsProvider = null)
        : this(new E2StoreWriter(new FileStream(path, FileMode.Create)), specProvider,
               Path.GetDirectoryName(path),
               beaconRootsProvider)
    {
    }

    /// <param name="outputStream">Output stream for the final assembled era file.</param>
    /// <param name="tempDirectory">
    /// Directory in which to create temp files for the component streams.
    /// When <c>null</c>, in-memory streams are used — suitable for tests and small eras
    /// where memory pressure is not a concern.
    /// </param>
    public EraWriter(Stream outputStream, ISpecProvider specProvider,
        string? tempDirectory = null,
        IBeaconRootsProvider? beaconRootsProvider = null)
        : this(new E2StoreWriter(outputStream), specProvider, tempDirectory, beaconRootsProvider)
    {
    }

    private EraWriter(E2StoreWriter e2StoreWriter, ISpecProvider specProvider,
        string? tempDirectory,
        IBeaconRootsProvider? beaconRootsProvider)
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

        if (tempDirectory is not null)
        {
            _tempFilePaths = new string?[3];
            _headersTempWriter = CreateTempWriter(tempDirectory, 0);
            _bodiesTempWriter = CreateTempWriter(tempDirectory, 1);
            _receiptsTempWriter = CreateTempWriter(tempDirectory, 2);
        }
        else
        {
            _headersTempWriter = new E2StoreWriter(new MemoryStream());
            _bodiesTempWriter = new E2StoreWriter(new MemoryStream());
            _receiptsTempWriter = new E2StoreWriter(new MemoryStream());
        }
    }

    private E2StoreWriter CreateTempWriter(string directory, int slot)
    {
        string path = Path.Combine(directory, Path.GetRandomFileName() + ".era.tmp");
        _tempFilePaths![slot] = path;
        return new E2StoreWriter(new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true));
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
        if (_count >= MaxEraSize)
            throw new ArgumentException($"Era file cannot contain more than {MaxEraSize} blocks.");

        if (_firstBlock)
        {
            _startNumber = block.Number;
            _blocksRootContext = new BlocksRootContext(block.Number, block.Header.Timestamp, _specProvider);
            _firstBlock = false;
        }
        else if (block.Number != _startNumber + _count)
        {
            throw new ArgumentException(
                $"Blocks must be added in sequential order. Expected block {_startNumber + _count}, got {block.Number}.",
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

        // Compress and write directly to the per-component temp stream.
        // _headerOffsets[i] is the byte position within the temp stream where entry i starts.
        using (NettyRlpStream headerRlp = _headerDecoder.EncodeToNewNettyStream(block.Header, rlpBehaviors))
        {
            _headerOffsets[_count] = _headersTempWriter.Position;
            await _headersTempWriter.WriteEntryAsSnappy(EntryTypes.CompressedHeader, headerRlp.AsMemory(), cancellation);
        }

        using (NettyRlpStream bodyRlp = _blockBodyDecoder.EncodeToNewNettyStream(block.Body, rlpBehaviors))
        {
            _bodyOffsets[_count] = _bodiesTempWriter.Position;
            await _bodiesTempWriter.WriteEntryAsSnappy(EntryTypes.CompressedBody, bodyRlp.AsMemory(), cancellation);
        }

        using (NettyRlpStream receiptsRlp = _slimReceiptDecoder.EncodeToNewNettyStream(receipts, rlpBehaviors))
        {
            _receiptsOffsets[_count] = _receiptsTempWriter.Position;
            await _receiptsTempWriter.WriteEntryAsSnappy(EntryTypes.CompressedSlimReceipts, receiptsRlp.AsMemory(), cancellation);
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

        _count++;
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
        int blockCount = _count;

        if (isTransitionEpoch)
        {
            for (int i = _preMergeBlockCount; i < blockCount; i++)
                _totalDifficulties[i] = _lastPreMergeTD;
        }

        long totalWritten = 0;
        long[] tdOffsets = needsTd ? ArrayPool<long>.Shared.Rent(blockCount) : [];
        try
        {
            totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.Version, Array.Empty<byte>(), cancellation);

            // Copy each component stream into the output, recording the base offset at which
            // each stream starts. The absolute position of block i's entry is base + tempOffset[i].
            long headersBase = totalWritten;
            totalWritten += await _e2StoreWriter.CopyFrom(_headersTempWriter.Stream, cancellation);

            long bodiesBase = totalWritten;
            totalWritten += await _e2StoreWriter.CopyFrom(_bodiesTempWriter.Stream, cancellation);

            long receiptsBase = totalWritten;
            totalWritten += await _e2StoreWriter.CopyFrom(_receiptsTempWriter.Stream, cancellation);

            if (needsTd)
            {
                for (int i = 0; i < blockCount; i++)
                {
                    tdOffsets[i] = totalWritten;
                    totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.TotalDifficulty, _totalDifficulties[i].ToLittleEndian(), cancellation);
                }
            }

            ValueHash256 accumulatorRoot = default;
            if (needsTd)
            {
                accumulatorRoot = _blocksRootContext!.AccumulatorRoot;
                totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.AccumulatorRoot, accumulatorRoot.ToByteArray(), cancellation);
            }

            // ComponentIndex
            // Layout: starting_number | [header_off, body_off, receipts_off, [td_off]] * N | component_count | block_count
            // All offsets are negative int64 LE, relative to start of the ComponentIndex TLV (including its 8-byte header).
            long componentIndexStart = totalWritten;
            int indexDataLength = IndexFieldSize + blockCount * componentCount * IndexFieldSize + IndexFieldSize + IndexFieldSize;

            using ArrayPoolList<byte> indexBytes = new(indexDataLength, indexDataLength);
            Span<byte> span = indexBytes.AsSpan();

            WriteInt64(span, 0, _startNumber);

            for (int i = 0; i < blockCount; i++)
            {
                int baseOff = IndexFieldSize + i * componentCount * IndexFieldSize;
                WriteInt64(span, baseOff + IndexFieldSize * 0, (headersBase + _headerOffsets[i]) - componentIndexStart);
                WriteInt64(span, baseOff + IndexFieldSize * 1, (bodiesBase + _bodyOffsets[i]) - componentIndexStart);
                WriteInt64(span, baseOff + IndexFieldSize * 2, (receiptsBase + _receiptsOffsets[i]) - componentIndexStart);
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

    private static void WriteInt64(Span<byte> destination, int off, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(off, IndexFieldSize), value);

    public void Dispose()
    {
        _blocksRootContext?.Dispose();
        _e2StoreWriter?.Dispose();
        _headersTempWriter.Dispose();
        _bodiesTempWriter.Dispose();
        _receiptsTempWriter.Dispose();
        _totalDifficulties.Dispose();

        if (_tempFilePaths is null) return;
        foreach (string? path in _tempFilePaths)
        {
            if (path is null) continue;
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }
}
