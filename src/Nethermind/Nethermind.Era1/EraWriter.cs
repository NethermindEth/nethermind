// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Cortex.SimpleSerialize;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Ssz;
using Snappier;

namespace Nethermind.Era1;
public class EraWriter : IDisposable
{
    public const int MaxEra1Size = 8192;

    private long _startNumber;
    private bool _firstBlock = true;
    private long _totalWritten;
    private readonly List<long> _entryIndexes = new();

    private readonly HeaderDecoder _headerDecoder = new();
    private readonly BlockBodyDecoder _blockBodyDecoder = new();
    private readonly ReceiptMessageDecoder _receiptDecoder = new();

    private readonly E2Store _e2Store;
    private readonly AccumulatorCalculator _accumulatorCalculator;
    private readonly ISpecProvider _specProvider;
    private bool _disposedValue;
    private bool _finalized;

    public static EraWriter Create(string path, ISpecProvider specProvider)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"'{nameof(path)}' cannot be null or whitespace.", nameof(path));
        return Create(new FileStream(path, FileMode.Create), specProvider);
    }
    public static EraWriter Create(Stream stream, ISpecProvider specProvider)
    {
        if (specProvider is null) throw new ArgumentNullException(nameof(specProvider));

        EraWriter b = new(new E2Store(stream), specProvider);
        return b;
    }

    private EraWriter(E2Store e2Store, ISpecProvider specProvider)
    {
        _e2Store = e2Store;
        _accumulatorCalculator = new();
        _specProvider = specProvider;
    }

    public Task<bool> Add(Block block, TxReceipt[] receipts, CancellationToken cancellation = default)
    {
        if (block.TotalDifficulty == null)
            throw new ArgumentException($"The block must have a {nameof(block.TotalDifficulty)}.", nameof(block));
        return Add(block, receipts, block.TotalDifficulty.Value, cancellation);
    }
    public Task<bool> Add(Block block, TxReceipt[] receipts, UInt256 totalDifficulty, CancellationToken cancellation = default)
    {
        if (block.Header == null)
            throw new ArgumentException("The block must have a header.", nameof(block));
        if (block.Hash == null)
            throw new ArgumentException("The block must have a hash.", nameof(block));

        Rlp encodedHeader = _headerDecoder.Encode(block.Header);
        Rlp encodedBody = _blockBodyDecoder.Encode(block.Body);
        //Geth implementation has a byte array representing both TxPostState and StatusCode, and serializes whatever is set
        RlpBehaviors behaviors = _specProvider.GetSpec(block.Header).IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None;
        Rlp encodedReceipts = _receiptDecoder.Encode(receipts, behaviors);

        return Add(block.Hash, encodedHeader.Bytes, encodedBody.Bytes, encodedReceipts.Bytes, block.Number, block.Difficulty, totalDifficulty, cancellation);
    }
    /// <summary>
    /// Write RLP encoded data to the underlying stream. 
    /// </summary>
    /// <param name="blockHash"></param>
    /// <param name="blockHeader"></param>
    /// <param name="blockBody"></param>
    /// <param name="receiptsArray"></param>
    /// <param name="blockNumber"></param>
    /// <param name="blockDifficulty"></param>
    /// <param name="totalDifficulty"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="EraException"></exception>
    public async Task<bool> Add(
        Hash256 blockHash,
        byte[] blockHeader,
        byte[] blockBody,
        byte[] receiptsArray,
        long blockNumber,
        UInt256 blockDifficulty,
        UInt256 totalDifficulty,
        CancellationToken cancellation = default)
    {
        if (blockHash is null) throw new ArgumentNullException(nameof(blockHash));
        if (blockHeader is null) throw new ArgumentNullException(nameof(blockHeader));
        if (blockBody is null) throw new ArgumentNullException(nameof(blockBody));
        if (receiptsArray is null) throw new ArgumentNullException(nameof(receiptsArray));
        if (totalDifficulty < blockDifficulty)
            throw new ArgumentOutOfRangeException(nameof(totalDifficulty), $"Cannot be less than the block difficulty.");
        if (_finalized)
            throw new EraException($"Finalized() has been called on this {nameof(EraWriter)}, and no more blocks can be added. ");

        if (_firstBlock)
        {
            _startNumber = blockNumber;
            _totalWritten += await WriteVersion();
            _firstBlock = false;
        }

        if (_entryIndexes.Count >= MaxEra1Size)
            return false;

        _entryIndexes.Add(_totalWritten);
        _accumulatorCalculator.Add(blockHash, totalDifficulty);
        //TODO possible optimize

        _totalWritten += await _e2Store.WriteEntryAsSnappy(EntryTypes.CompressedHeader, blockHeader, cancellation);

        _totalWritten += await _e2Store.WriteEntryAsSnappy(EntryTypes.CompressedBody, blockBody, cancellation);

        _totalWritten += await _e2Store.WriteEntryAsSnappy(EntryTypes.CompressedReceipts, receiptsArray, cancellation);

        _totalWritten += await _e2Store.WriteEntry(EntryTypes.TotalDifficulty, totalDifficulty.ToLittleEndian(), cancellation);

        return true;
    }

    public async Task<byte[]> Finalize(CancellationToken cancellation = default)
    {
        if (_firstBlock)
            throw new EraException("Finalize was called, but no blocks have been added yet.");

        byte[] root = _accumulatorCalculator.ComputeRoot().ToArray();
        _totalWritten += await _e2Store.WriteEntry(EntryTypes.Accumulator, root, cancellation);

        //Index is 64 bits segments in the format => start | index | index | ... | count
        //16 bytes is for the start and count plus every entry 
        byte[] blockIndex = new byte[16 + _entryIndexes.Count * 8];
        WriteUInt64(blockIndex, 0, (ulong)_startNumber);

        //era1:= Version | block-tuple ... | other-entries ... | Accumulator | BlockIndex
        //block-index := starting-number | index | index | index... | count

        long firstIndexEnd = _totalWritten + 2 * 8;

        //All positions are relative to the end position in the index
        for (int i = 0; i < _entryIndexes.Count; i++)
        {
            long relativePosition = _entryIndexes[i] - 8 - (firstIndexEnd + i * 8);
            //Skip 8 bytes for the start value
            WriteUInt64(blockIndex, 8 + i * 8, (ulong)relativePosition);
        }

        WriteUInt64(blockIndex, 8 + _entryIndexes.Count * 8, (ulong)_entryIndexes.Count);

        await _e2Store.WriteEntry(EntryTypes.BlockIndex, blockIndex, cancellation);
        await _e2Store.Flush(cancellation);

        _entryIndexes.Clear();
        _accumulatorCalculator.Clear();
        _finalized = true;
        return root;
    }
    private static bool WriteUInt64(byte[] destination, int off, ulong value)
    {
        return BitConverter.TryWriteBytes(new Span<byte>(destination, off, 8), value) == false ? throw new EraException("Failed to write UInt64 to output.") : true;
    }

    private Task<int> WriteVersion()
    {
        return _e2Store.WriteEntry(EntryTypes.Version, Array.Empty<byte>());
    }

    private class EntryIndexInfo
    {
        public long Index { get; }
        public EntryIndexInfo(long index)
        {
            Index = index;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _e2Store?.Dispose();
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public static string Filename(string network, int epoch, Hash256 root)
    {
        if (string.IsNullOrEmpty(network)) throw new ArgumentException($"'{nameof(network)}' cannot be null or empty.", nameof(network));
        if (root is null) throw new ArgumentNullException(nameof(root));
        if (epoch < 0) throw new ArgumentOutOfRangeException(nameof(epoch), "Cannot be a negative number.");

        return $"{network}-{epoch.ToString("D5")}-{root.ToString(true)[2..10]}.era1";
    }
}
