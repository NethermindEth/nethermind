// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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

    internal EraBuilder(string file)
    {
        _fileStream = File.OpenWrite(file);
        //TODO FIX
        _e2Store = E2Store.FromStream(_fileStream).Result;
    }

    public async Task<bool> Add(Block block, TxReceipt[] receipts, UInt256 totalDifficulty, CancellationToken cancellation = default)
    {
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
        _totalWritten += await _e2Store.WriteEntryAsSnappy(EntryTypes.TypeCompressedHeader, encodedHeader.Bytes, cancellation);

        Rlp encodedBody = _blockBodyDecoder.Encode(block.Body);
        _totalWritten += await _e2Store.WriteEntryAsSnappy(EntryTypes.TypeCompressedBody, encodedBody.Bytes, cancellation);

        Rlp encodedReceipts = _receiptDecoder.Encode(receipts);
        _totalWritten += await _e2Store.WriteEntryAsSnappy(EntryTypes.TypeCompressedReceipts, encodedReceipts.Bytes, cancellation);

        _totalWritten += await _e2Store.WriteEntry(EntryTypes.TypeTotalDifficulty, totalDifficulty.ToLittleEndian(), cancellation);

        return true;
    }

    public async Task Finalize()
    {
        if (_firstBlock)
            throw new EraException("Finalize was called,but no blocks have been added yet.");

        await _fileStream.FlushAsync();
        _entryIndexInfos.Clear();
    }

    private Keccak CalculateAccumulator()
    {
        //TODO optmize
        var buf = new byte[32];
        foreach (var info in _entryIndexInfos)
        {
        }
        return null;
    }

    private Task WriteVersion()
    {
        return _e2Store.WriteEntry(EntryTypes.TypeVersion, Array.Empty<byte>());
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
