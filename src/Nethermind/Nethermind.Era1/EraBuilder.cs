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
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Ssz;
using Snappier;

namespace Nethermind.Era1;
internal class EraBuilder:IDisposable
{
    private const int MaxEra1Size = 8192;

    private long _startNumber;
    private bool _firstBlock = true;
    private UInt256 _startTd;
    private long _totalWritten;
    private List<EntryIndexInfo> _entryIndexInfos = new List<EntryIndexInfo>();

    private BlockDecoder _blockDecoder = new BlockDecoder();
    private BlockBodyDecoder _blockBodyDecoder = new BlockBodyDecoder();
    private HeaderDecoder _headerDecoder = new HeaderDecoder();
    private ReceiptDecoder _receiptDecoder = new ReceiptDecoder();

    private FileStream _fileStream;
    private E2Store _e2Store;
    private bool _disposedValue;
    private bool _finalized;

    internal EraBuilder(string file)
    {
        _fileStream = new FileStream(file, FileMode.Create);
        //TODO FIX
        _e2Store = E2Store.ForWrite(_fileStream).Result;
    }

    public async Task<bool> Add(Block block, TxReceipt[] receipts, UInt256 totalDifficulty, CancellationToken cancellation = default)
    {
        if (_finalized)
            throw new EraException($"Finalized has been called on this {nameof(EraBuilder)}, and no more blocks can be added. ");

        if (block.Header == null)
            throw new ArgumentException("The block must have a header.", nameof(block));

        if (block.Hash == null)
            throw new ArgumentException("The block must have a hash.", nameof(block));

        if (_firstBlock)
        {
            _startNumber = block.Number;
            _startTd = totalDifficulty - block.Difficulty;
            await WriteVersion();
            _firstBlock = false;
        }

        if (_entryIndexInfos.Count >= MaxEra1Size)
            return false;

        _entryIndexInfos.Add(new EntryIndexInfo(_totalWritten, block.Hash, totalDifficulty));
        //TODO possible optimize

        Rlp encodedHeader = _headerDecoder.Encode(block.Header);
        _totalWritten += await _e2Store.WriteEntryAsSnappy(EntryTypes.CompressedHeader, encodedHeader.Bytes, cancellation);

        Rlp encodedBody = _blockBodyDecoder.Encode(block.Body);
        _totalWritten += await _e2Store.WriteEntryAsSnappy(EntryTypes.CompressedBody, encodedBody.Bytes, cancellation);

        Rlp encodedReceipts = _receiptDecoder.Encode(receipts);
        _totalWritten += await _e2Store.WriteEntryAsSnappy(EntryTypes.CompressedReceipts, encodedReceipts.Bytes, cancellation);

        _totalWritten += await _e2Store.WriteEntry(EntryTypes.TotalDifficulty, totalDifficulty.ToLittleEndian(), cancellation);

        return true;
    }

    public async Task Finalize()
    {
        if (_firstBlock)
            throw new EraException("Finalize was called, but no blocks have been added yet.");

        byte[] root = CalculateAccumulator().ToArray();
        _totalWritten += await _e2Store.WriteEntry(EntryTypes.Accumulator, root);

        //Index is 64 bits segments in the format => start | index | index | ... | count
        //16 bytes for start and count plus every entry 
        byte[] index = new byte[16 + _entryIndexInfos.Count * 8];
        if (!TryWriteUInt64(index, 0, (ulong)_startNumber))
        {
            //TODO handle
        }

        long absoluteIndexStart = _totalWritten + 3 * 8;

        //All positions are relative to the position in the index
        for (int i = 0; i < _entryIndexInfos.Count; i++)
        {
            long relativePosition = _entryIndexInfos[i].Index - (absoluteIndexStart + i * 8);
            //Skip 8 bytes for the start value
            if (TryWriteUInt64(index, 8 + i * 8, (ulong)relativePosition))
            {
                //TODO handle
            }

        }

        if (!TryWriteUInt64(index, 8 + _entryIndexInfos.Count * 8, (ulong)_entryIndexInfos.Count))
        {
            //TODO handle
        }
        await _e2Store.WriteEntry(EntryTypes.BlockIndex, index);

        await _fileStream.FlushAsync();
        _entryIndexInfos.Clear();
        _finalized = true;
    }
    private static bool TryWriteUInt64(byte[] destination, int off, ulong value)
    {
        return BitConverter.TryWriteBytes(new Span<byte>(destination, off, 8), value);
    }

    private ReadOnlySpan<byte> CalculateAccumulator()
    {
        //TODO optmize
        List<SszComposite> roots = new(_entryIndexInfos.Count);
        List<SszElement> sszElements = new(2);
        foreach (var info in _entryIndexInfos)
        {
            sszElements.Add(new SszBasicVector(info.Hash.Bytes));
            sszElements.Add(new SszBasicVector(info.TotalDifficulty.ToLittleEndian()));
            SszTree tree = new ( new SszContainer(sszElements));
            roots.Add(new SszBasicVector(tree.HashTreeRoot()));
            sszElements.Clear();
        }
        return new SszTree(new SszList(roots, (ulong)MaxEra1Size)).HashTreeRoot();
    }

    private Task WriteVersion()
    {
        return _e2Store.WriteEntry(EntryTypes.Version, Array.Empty<byte>());
    }

    private struct EntryIndexInfo
    {
        public long Index;
        public Keccak Hash;
        public UInt256 TotalDifficulty;

        public EntryIndexInfo(long index, Keccak hash, UInt256 totalDifficulty)
        {
            Index = index;
            Hash = hash;
            TotalDifficulty = totalDifficulty;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _e2Store?.Dispose();
                _fileStream?.Dispose();
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
}
