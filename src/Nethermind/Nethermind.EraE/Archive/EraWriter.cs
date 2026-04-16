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
/// Each call to <see cref="Add"/> encodes and buffers the block's header, body, and receipts.
/// <see cref="Finalize"/> writes the buffered components in EraE section order:
/// Version | CompressedHeader* | CompressedBody* | CompressedSlimReceipts* |
/// TotalDifficulty* | AccumulatorRoot? | ComponentIndex.
/// </summary>
public sealed class EraWriter : IDisposable
{
    public const int MaxEraSize = 8192;

    private const long MillisecondsPerSecond = 1000;
    private const int BeaconSlotSeconds = 12;
    private const int IndexFieldSize = 8; // sizeof(long) — each ComponentIndex field is a little-endian int64

    private readonly HeaderDecoder _headerDecoder = new();
    private readonly BlockBodyDecoder _blockBodyDecoder = new();
    private readonly E2StoreWriter _e2StoreWriter;
    private readonly ISpecProvider _specProvider;
    private readonly IBeaconRootsProvider? _beaconRootsProvider;
    private readonly SlotTime? _slotTime;

    // Buffered per-block RLP payloads. These are written in section order during Finalize().
    private readonly ArrayPoolList<byte[]> _headers = new(MaxEraSize);
    private readonly ArrayPoolList<byte[]> _bodies = new(MaxEraSize);
    private readonly ArrayPoolList<byte[]> _receipts = new(MaxEraSize);

    // Per-block byte offsets recorded during Finalize() and written into the ComponentIndex.
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
            throw new EraException("Finalize() has been called; no more blocks can be added.");
        ArgumentNullException.ThrowIfNull(block.Header);
        ArgumentNullException.ThrowIfNull(block.Hash);
        if (_headers.Count >= MaxEraSize)
            throw new ArgumentException($"Era file cannot contain more than {MaxEraSize} blocks.");

        if (_firstBlock)
        {
            _startNumber = block.Number;
            _blocksRootContext = new BlocksRootContext(block.Number, block.Header.Timestamp, _specProvider);
            _firstBlock = false;
            await _e2StoreWriter.WriteEntry(EntryTypes.Version, Memory<byte>.Empty, cancellation);
        }
        else if (block.Number != _startNumber + _headers.Count)
        {
            throw new ArgumentException(
                $"Blocks must be added in sequential order. Expected block {_startNumber + _headers.Count}, got {block.Number}.",
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
            _headers.Add(headerRlp.AsMemory().ToArray());
        }

        using (NettyRlpStream bodyRlp = _blockBodyDecoder.EncodeToNewNettyStream(block.Body, rlpBehaviors))
        {
            _bodies.Add(bodyRlp.AsMemory().ToArray());
        }

        _receipts.Add(EncodeSlimReceipts(receipts, spec.IsEip658Enabled));

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
        int blockCount = _headers.Count;

        if (isTransitionEpoch)
        {
            for (int i = _preMergeBlockCount; i < blockCount; i++)
                _totalDifficulties[i] = _lastPreMergeTD;
        }

        long[] tdOffsets = needsTd ? ArrayPool<long>.Shared.Rent(blockCount) : [];
        try
        {
            // Write sections in EraE spec order:
            // Version | Header* | Body* | Receipts* | TD* | Accumulator? | ComponentIndex

            for (int i = 0; i < blockCount; i++)
            {
                _headerOffsets.Add(_e2StoreWriter.Position);
                await WriteCompressed(EntryTypes.CompressedHeader, _headers[i], cancellation);
            }

            for (int i = 0; i < blockCount; i++)
            {
                _bodyOffsets.Add(_e2StoreWriter.Position);
                await WriteCompressed(EntryTypes.CompressedBody, _bodies[i], cancellation);
            }

            for (int i = 0; i < blockCount; i++)
            {
                _receiptsOffsets.Add(_e2StoreWriter.Position);
                await WriteCompressed(EntryTypes.CompressedSlimReceipts, _receipts[i], cancellation);
            }

            if (needsTd)
            {
                for (int i = 0; i < blockCount; i++)
                {
                    tdOffsets[i] = _e2StoreWriter.Position;
                    await _e2StoreWriter.WriteEntry(
                        EntryTypes.TotalDifficulty,
                        _totalDifficulties[i].ToLittleEndian(),
                        cancellation);
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
            if (needsTd)
                ArrayPool<long>.Shared.Return(tdOffsets);
        }
    }

    public void Dispose()
    {
        _blocksRootContext?.Dispose();
        _e2StoreWriter?.Dispose();
        _headers.Dispose();
        _bodies.Dispose();
        _receipts.Dispose();
        _headerOffsets.Dispose();
        _bodyOffsets.Dispose();
        _receiptsOffsets.Dispose();
        _totalDifficulties.Dispose();
    }

    /// <summary>
    /// Encodes receipts in the go-ethereum slim format used by EraE files:
    /// rlp([txType, postStateOrStatus, cumulativeGas, logs]) per receipt, no bloom.
    /// txType is empty-bytes for legacy (type 0), single byte otherwise.
    /// postStateOrStatus is the 32-byte state root pre-EIP-658, or 0x01/empty for success/failure post-EIP-658.
    /// </summary>
    private static byte[] EncodeSlimReceipts(TxReceipt[] receipts, bool isEip658)
    {
        int totalLength = 0;
        foreach (TxReceipt receipt in receipts)
            totalLength += Rlp.LengthOfSequence(GetReceiptContentLength(receipt, isEip658));

        RlpStream stream = new(Rlp.LengthOfSequence(totalLength));
        stream.StartSequence(totalLength);
        foreach (TxReceipt receipt in receipts)
            WriteReceipt(stream, receipt, isEip658);
        return stream.Data.ToArray() ?? [];
    }

    private static int GetReceiptContentLength(TxReceipt receipt, bool isEip658)
    {
        int logsLength = 0;
        if (receipt.Logs is not null)
            foreach (LogEntry log in receipt.Logs)
                logsLength += LogEntryDecoder.Instance.GetLength(log);

        // txType: 0x80 (empty = legacy) or single self-encoding byte (type 1/2/3) — always 1 byte
        int statusLength = isEip658 ? 1 : Rlp.LengthOf(receipt.PostTransactionState);

        return 1 + statusLength + Rlp.LengthOf(receipt.GasUsedTotal) + Rlp.LengthOfSequence(logsLength);
    }

    private static void WriteReceipt(RlpStream stream, TxReceipt receipt, bool isEip658)
    {
        int logsLength = 0;
        if (receipt.Logs is not null)
        {
            foreach (LogEntry log in receipt.Logs)
                logsLength += LogEntryDecoder.Instance.GetLength(log);
        }

        int statusLength = isEip658 ? 1 : Rlp.LengthOf(receipt.PostTransactionState);
        int contentLength = 1 + statusLength + Rlp.LengthOf(receipt.GasUsedTotal) + Rlp.LengthOfSequence(logsLength);

        stream.StartSequence(contentLength);

        // TxType: empty byte array for legacy, single byte for typed (EIP-2718)
        if (receipt.TxType == TxType.Legacy)
            stream.Encode(Array.Empty<byte>());
        else
            stream.WriteByte((byte)receipt.TxType);

        // postStateOrStatus: 32-byte hash (pre-EIP-658), 0x01 (success), or empty (failure)
        if (!isEip658)
            stream.Encode(receipt.PostTransactionState);
        else if (receipt.StatusCode == 0)
            stream.Encode(Array.Empty<byte>());
        else
            stream.WriteByte(receipt.StatusCode);

        stream.Encode(receipt.GasUsedTotal);

        stream.StartSequence(logsLength);
        if (receipt.Logs is not null)
        {
            foreach (LogEntry log in receipt.Logs)
                LogEntryDecoder.Instance.Encode(stream, log);
        }
    }

    private async Task WriteCompressed(ushort entryType, ReadOnlyMemory<byte> data, CancellationToken cancellation)
    {
        await using RecyclableMemoryStream ms = RecyclableStream.GetStream(nameof(EraWriter));
        await using (SnappyStream compressor = new(ms, CompressionMode.Compress, leaveOpen: true))
        {
            compressor.Write(data.Span);
            await compressor.FlushAsync(cancellation);
        }

        bool ok = ms.TryGetBuffer(out ArraySegment<byte> segment);
        Debug.Assert(ok);
        await _e2StoreWriter.WriteEntry(entryType, segment.AsMemory(), cancellation);
    }

    private static void WriteInt64(Span<byte> destination, int off, long value) =>
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(off, IndexFieldSize), value);
}
