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
/// Each call to <see cref="Add"/> immediately compresses the block's header, body, and receipts
/// via Snappy and buffers the compressed bytes. This keeps peak memory per epoch proportional
/// to compressed data size (~10–50 MB for pre-merge, ~150–400 MB for post-merge mainnet),
/// rather than the uncompressed RLP size. <see cref="Finalize"/> writes the buffered
/// compressed entries to the output stream in a single sequential pass.
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

    // Compressed-and-rented buffers. Entries are Snappy-compressed in Add() so that
    // the in-memory footprint reflects compressed sizes, not raw RLP sizes.
    private readonly ArrayPoolList<(byte[] Buffer, int Length)> _compressedHeaders = new(MaxEraSize);
    private readonly ArrayPoolList<(byte[] Buffer, int Length)> _compressedBodies = new(MaxEraSize);
    private readonly ArrayPoolList<(byte[] Buffer, int Length)> _compressedReceipts = new(MaxEraSize);

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
        if (_compressedHeaders.Count >= MaxEraSize)
            throw new ArgumentException($"Era file cannot contain more than {MaxEraSize} blocks.");

        if (_firstBlock)
        {
            _startNumber = block.Number;
            _blocksRootContext = new BlocksRootContext(block.Number, block.Header.Timestamp, _specProvider);
            _firstBlock = false;
        }
        else if (block.Number != _startNumber + _compressedHeaders.Count)
        {
            throw new ArgumentException(
                $"Blocks must be added in sequential order. Expected block {_startNumber + _compressedHeaders.Count}, got {block.Number}.",
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

        // Compress immediately so buffered memory reflects compressed sizes (~10x smaller than raw RLP).
        using (NettyRlpStream headerRlp = _headerDecoder.EncodeToNewNettyStream(block.Header, rlpBehaviors))
            _compressedHeaders.Add(await E2StoreWriter.Compress(headerRlp.AsMemory(), cancellation));

        using (NettyRlpStream bodyRlp = _blockBodyDecoder.EncodeToNewNettyStream(block.Body, rlpBehaviors))
            _compressedBodies.Add(await E2StoreWriter.Compress(bodyRlp.AsMemory(), cancellation));

        using (NettyRlpStream receiptsRlp = _slimReceiptDecoder.EncodeToNewNettyStream(receipts, rlpBehaviors))
            _compressedReceipts.Add(await E2StoreWriter.Compress(receiptsRlp.AsMemory(), cancellation));

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
        int blockCount = _compressedHeaders.Count;

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
            totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.Version, Array.Empty<byte>(), cancellation);

            for (int i = 0; i < blockCount; i++)
            {
                headerOffsets[i] = totalWritten;
                (byte[] buf, int len) = _compressedHeaders[i];
                totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.CompressedHeader, buf.AsMemory(0, len), cancellation);
            }

            for (int i = 0; i < blockCount; i++)
            {
                bodyOffsets[i] = totalWritten;
                (byte[] buf, int len) = _compressedBodies[i];
                totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.CompressedBody, buf.AsMemory(0, len), cancellation);
            }

            for (int i = 0; i < blockCount; i++)
            {
                receiptsOffsets[i] = totalWritten;
                (byte[] buf, int len) = _compressedReceipts[i];
                totalWritten += await _e2StoreWriter.WriteEntry(EntryTypes.CompressedSlimReceipts, buf.AsMemory(0, len), cancellation);
            }

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
            // Offsets are negative int64 LE, relative to start of the ComponentIndex TLV (including 8-byte header).
            long componentIndexStart = totalWritten;
            int indexDataLength = IndexFieldSize + blockCount * componentCount * IndexFieldSize + IndexFieldSize + IndexFieldSize;

            using ArrayPoolList<byte> indexBytes = new(indexDataLength, indexDataLength);
            Span<byte> span = indexBytes.AsSpan();

            WriteInt64(span, 0, _startNumber);

            for (int i = 0; i < blockCount; i++)
            {
                int baseOff = IndexFieldSize + i * componentCount * IndexFieldSize;
                WriteInt64(span, baseOff + IndexFieldSize * 0, headerOffsets[i] - componentIndexStart);
                WriteInt64(span, baseOff + IndexFieldSize * 1, bodyOffsets[i] - componentIndexStart);
                WriteInt64(span, baseOff + IndexFieldSize * 2, receiptsOffsets[i] - componentIndexStart);
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
            ArrayPool<long>.Shared.Return(headerOffsets);
            ArrayPool<long>.Shared.Return(bodyOffsets);
            ArrayPool<long>.Shared.Return(receiptsOffsets);
            if (needsTd) ArrayPool<long>.Shared.Return(tdOffsets);
        }
    }

    private static void WriteInt64(Span<byte> destination, int off, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(off, IndexFieldSize), value);

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
        ReturnBuffers(_compressedHeaders);
        ReturnBuffers(_compressedBodies);
        ReturnBuffers(_compressedReceipts);
        _totalDifficulties.Dispose();
    }
}
