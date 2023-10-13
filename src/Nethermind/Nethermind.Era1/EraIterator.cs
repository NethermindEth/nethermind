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
internal class EraIterator : IAsyncEnumerable<(Block, TxReceipt[])>, IDisposable
{
    private E2Store _store;
    private bool _disposedValue;
    private long _currentBlockIndex;
    private UInt256 _currentTotalDiffulty;

    public long CurrentBlockIndex => _currentBlockIndex;

    private EraIterator(E2Store e2)
    {
        _store = e2;
        _currentBlockIndex = e2.Metadata.Start;
    }
    public async IAsyncEnumerator<(Block, TxReceipt[])> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        (Block? b, TxReceipt[]? r) = await Next(cancellationToken);
        while(b != null && r != null)
        {
            yield return (b, r)!;
            (b, r) = await Next(cancellationToken);
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

        EraIterator e = new EraIterator(await E2Store.FromStream(stream, token));
        return e;
    }
    private async Task<(Block?, TxReceipt[]?)> Next(CancellationToken cancellationToken)
    {
        if (_store.Metadata.Start + _store.Metadata.Count <= _currentBlockIndex)
        {
            //TODO test enumerate more than once
            Reset();
            return (null, null);
        }
        long blockOffset = await FindBlockOffset(_currentBlockIndex, cancellationToken);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(E2Store.ValueSizeLimit);
        try
        {
            Debug.WriteLine($"Reading block entry at index {_currentBlockIndex}");
            (int read, Entry e) = await _store.ReadEntryAt(blockOffset, cancellationToken);
            CheckType(e, EntryTypes.TypeCompressedHeader);
            BlockHeader header = await DecompressAndDecode<BlockHeader>(buffer, e);
            var headerEntry = e;

            (read, e) = await _store.ReadEntryCurrentPosition(cancellationToken);
            CheckType(e, EntryTypes.TypeCompressedBody);

            BlockBody body = await DecompressAndDecode<BlockBody>(buffer, e);

            (read, e) = await _store.ReadEntryCurrentPosition(cancellationToken);
            CheckType(e, EntryTypes.TypeCompressedReceipts);

            read = await Decompress(buffer, e);
            TxReceipt[] receipts = DecodeReceipts(buffer, 0, read);
            

            (read, e) = await _store.ReadEntryCurrentPosition(cancellationToken);
            CheckType(e, EntryTypes.TypeTotalDifficulty);
            read = await _store.ReadEntryValue(buffer, e, cancellationToken);
            
            _currentTotalDiffulty = new UInt256(new ArraySegment<byte>(buffer, 0, read));

            Block block = new Block(header, body);
            
            _currentBlockIndex++;
            return (block, receipts);
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
        return new ReceiptDecoder().DecodeArray(b.AsRlpStream());
    }

    private async Task<long> FindBlockOffset(long blockIndex, CancellationToken token = default)
    {
        long firstIndex = -8 - _store.Metadata.Count * 8;
        long indexOffset = (blockIndex - _store.Metadata.Start) * 8;
        long offOffset = _store.Metadata.Length + firstIndex + indexOffset;

        long blockOffset = await _store.ReadValueAt(offOffset, token);
        return offOffset + 8 + blockOffset;
    }

    private void Reset()
    {
        _currentBlockIndex = 0;
        _currentTotalDiffulty = 0;
    }


    private static void CheckType(Entry e, ushort expected)
    {
        if (e.Type != expected)
            throw new EraException($"Expected an entry of type {expected}, but got {e.Type}.");
    }
    private Task<int> Decompress(byte[] buffer, Entry e, CancellationToken cancellation = default)
    {
        return _store.ReadEntryValueAsSnappy(buffer, e, cancellation);
    }

    private async Task<T> DecompressAndDecode<T>(byte[] buffer, Entry e, CancellationToken cancellation = default) where T : class
    {
        //TODO handle read more than buffer length
        var bufferRead = await _store.ReadEntryValueAsSnappy(buffer, e, cancellation);
        T? decoded = RlpDecode<T>(buffer, 0, bufferRead);
        //TODO remove
        switch (typeof(T).Name)
        {
            case nameof(BlockHeader):
                Rlp encodedHeader = new HeaderDecoder().Encode((BlockHeader)Convert.ChangeType(decoded, typeof(BlockHeader)));
                if (!ByteArrayCompare(buffer, encodedHeader.Bytes, bufferRead))
                {
                    throw new Exception("not equal");
                }
                break;
            case nameof(BlockBody):
                Rlp encodedBody = new BlockBodyDecoder().Encode((BlockBody)Convert.ChangeType(decoded, typeof(BlockBody)));
                if (!ByteArrayCompare(buffer, encodedBody.Bytes, bufferRead))
                {
                    throw new Exception("not equal");
                }
                break;

            default:
                break;
        }
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
        //Debug.WriteLine($"Rlp decoded {typeof(T).Name} {BitConverter.ToString(new ArraySegment<byte>(buffer, offset, count).ToArray()).Replace("-","")}");
        return decoded;
    }

    private bool IsValidFilename(string file)
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
