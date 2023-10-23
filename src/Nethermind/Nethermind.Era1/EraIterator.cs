// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Snappier;

namespace Nethermind.Era1;
internal class EraIterator : IAsyncEnumerable<(Block, TxReceipt[], UInt256)>, IDisposable
{
    private bool _disposedValue;
    private long _currentBlockNumber;
    private ReceiptStorageDecoder _receiptStorageDecoder = new();
    private E2Store _store;

    public long CurrentBlockNumber => _currentBlockNumber;

    private EraIterator(E2Store e2)
    {
        _store = e2;
        _currentBlockNumber = e2.Metadata.Start;
    }
    public async IAsyncEnumerator<(Block, TxReceipt[], UInt256)> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        (Block? b, TxReceipt[]? r, UInt256? td) = await Next(cancellationToken);
        while(b != null && r != null && td != null)
        {
            yield return (b, r, td.Value);
            (b, r, td) = await Next(cancellationToken);
        }
    }
    internal static Task<EraIterator> Create(string file, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(file)) throw new ArgumentException("Cannot be null or empty.", nameof(file));
        return Create(File.OpenRead(file), token);
    }
    internal static async Task<EraIterator> Create(Stream stream, CancellationToken token = default)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead) throw new ArgumentException("Provided stream is not readable.");

        E2Store e2 = new E2Store(stream);
        await e2.SetMetaData();
        EraIterator e = new EraIterator(e2);
        
        return e;
    }
    private async Task<(Block?, TxReceipt[]?, UInt256?)> Next(CancellationToken cancellationToken)
    {
        if (_store.Metadata.Start + _store.Metadata.Count <= _currentBlockNumber)
        {
            //TODO test enumerate more than once
            Reset();
            return (null, null, null);
        }
        long blockOffset = await SeekToBlock(_currentBlockNumber, cancellationToken);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(E2Store.ValueSizeLimit);
        try
        {
            Entry e = await _store.ReadEntryAt(blockOffset, cancellationToken);
            CheckType(e, EntryTypes.CompressedHeader);
            BlockHeader header = await DecompressAndDecode<BlockHeader>(buffer, 0, e);

            e = await _store.ReadEntryCurrentPosition(cancellationToken);
            CheckType(e, EntryTypes.CompressedBody);

            BlockBody body = await DecompressAndDecode<BlockBody>(buffer, 0, e);

            e = await _store.ReadEntryCurrentPosition(cancellationToken);
            CheckType(e, EntryTypes.CompressedReceipts);

            int read = await Decompress(buffer, 0, e);
            TxReceipt[] receipts = DecodeReceipts(buffer, 0, read);

            e = await _store.ReadEntryCurrentPosition(cancellationToken);
            CheckType(e, EntryTypes.TotalDifficulty);
            read = await _store.ReadEntryValue(buffer, e, cancellationToken);
            
            UInt256 currentTotalDiffulty = new UInt256(new ArraySegment<byte>(buffer, 0, read));

            Block block = new Block(header, body);

            _currentBlockNumber++;
            return (block, receipts, currentTotalDiffulty);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        };
    }

    private TxReceipt[] DecodeReceipts(byte[] buf, int off, int count)
    {
        //TODO optimize
        var b = new byte[count];
        new Span<byte>(buf, off, count).CopyTo(b);
        return _receiptStorageDecoder.DecodeArray(b.AsRlpStream());
    }

    private async Task<long> SeekToBlock(long blockNumber, CancellationToken token = default)
    {
        //Last 8 bytes is the count, so we skip them
        long startOfIndex = _store.Metadata.Length - 8 - _store.Metadata.Count * 8;
        long indexOffset = (blockNumber - _store.Metadata.Start) * 8;
        long blockIndexOffset =  startOfIndex + indexOffset;

        long blockOffset = await _store.ReadValueAt(blockIndexOffset, token);
        return _store.Seek(blockOffset, SeekOrigin.Current);
    }

    private void Reset()
    {
        _currentBlockNumber = 0;
    }


    private static void CheckType(Entry e, ushort expected)
    {
        if (e.Type != expected)
            throw new EraException($"Expected an entry of type {expected}, but got {e.Type}.");
    }
    private Task<int> Decompress(byte[] buffer, int offset, Entry e, CancellationToken cancellation = default)
    {
        return _store.ReadEntryValueAsSnappy(buffer, offset, e, cancellation);
    }

    private async Task<T> DecompressAndDecode<T>(byte[] buffer, int offset, Entry e, CancellationToken cancellation = default) where T : class
    {
        //TODO handle read more than buffer length
        var bufferRead = await _store.ReadEntryValueAsSnappy(buffer, offset, e, cancellation);
        T? decoded = RlpDecode<T>(buffer, 0, bufferRead);
        return decoded;
    }

    //TODO REMOVE
    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int memcmp(byte[] b1, byte[] b2, long count);
    static bool ByteArrayCompare(byte[] b1, byte[] b2, long count)
    {
        return memcmp(b1, b2, count) == 0;
    }

    private static T RlpDecode<T>(byte[] buffer, int offset, int count)
    {
        T? decoded = Rlp.Decode<T>(new Span<byte>(buffer, offset, count));
        return decoded;
    }

    private static bool IsValidFilename(string file)
    {
        if (!Path.GetExtension(file).Equals(".era1", StringComparison.OrdinalIgnoreCase))
            return false;
        string[] parts = file.Split(new char[] { '-' });
        uint epoch;
        if (parts.Length != 3 || !uint.TryParse(parts[1], out epoch))
            return false;
        return true;
    }

    private static void ThrowInvalidFileName(string filename)
    {
        throw new EraException($"Invalid era1 filename: {filename}");
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _store?.Dispose();
            }
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
