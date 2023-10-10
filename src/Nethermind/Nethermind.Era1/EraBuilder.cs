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
using Snappier;

namespace Nethermind.Era1;
internal class EraBuilder:IDisposable
{
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
    private SnappyStream _snappyStream;
    private E2Store _e2Store;
    private bool _disposedValue;

    internal EraBuilder(string file)
    {
        _fileStream = File.OpenWrite(file);
        _snappyStream = new SnappyStream(_fileStream, System.IO.Compression.CompressionMode.Compress);
        //TODO FIX
        _e2Store = E2Store.FromStream(_fileStream).Result;
    }

    public Task Add(Block block, TxReceipt[] receipts, UInt256 totalDifficulty)
    {
        if (_firstBlock)
        {
            _firstBlock = false;

            _startNumber = block.Number;
            _totalWritten = _startNumber;
            _startTd = totalDifficulty - block.Difficulty;
        }
        Rlp encodedHeader = _headerDecoder.Encode(block.Header);

        Rlp encodedBody = _blockBodyDecoder.Encode(block.Body);
        NettyRlpStream encodedReceipt = _receiptDecoder.EncodeToNewNettyStream(receipts);
        
        throw new NotImplementedException();
    }

    private Task WriteVersion()
    {
        return _e2Store.WriteEntry(E2Store.TypeVersion, Array.Empty<byte>(), 0);
    }

    private struct EntryIndexInfo
    {
        public int Index;
        public Keccak Hash;
        public UInt256 TotalDifficulty;
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
