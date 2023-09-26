// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Snappier;

namespace Nethermind.Era1;
internal class EraIterator : IAsyncEnumerable<(Block, TxReceipt[])>, IDisposable
{
    private E2Store _store;
    private bool _disposedValue;
    private int _currentBlockIndex;
    private UInt256 _currentTotalDiffulty;

    public int CurrentBlockIndex => _currentBlockIndex;

    private EraIterator(E2Store e2)
    {
        _store = e2;
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
    internal static async Task<EraIterator> Create(string file, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(file)) throw new ArgumentException("Cannot be null or empty.", nameof(file));

        EraIterator e = new EraIterator(await E2Store.FromStream(File.OpenRead(file), token));
        return e;
    }
    private async Task<(Block?, TxReceipt[]?)> Next(CancellationToken cancellationToken)
    {
        if (_store.Metadata.Start + _store.Metadata.Count <= _currentBlockIndex)
        {
            Reset();
            return (null, null);
        }
        long blockOffset = await FindBlockOffset(_currentBlockIndex, cancellationToken);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(E2Store.ValueSizeLimit);
        try
        {
            (int read, Entry e) = await _store.ReadEntryAt(blockOffset, cancellationToken);
            CheckType(e, E2Store.TypeCompressedHeader);
            blockOffset += read;

            BlockHeader header = DecompressAndDecode<BlockHeader>(buffer, e);
            var headerEntry = e;
            (read, e) = await _store.ReadEntryAt(blockOffset, cancellationToken);
            CheckType(e, E2Store.TypeCompressedBody);
            blockOffset += read;

            BlockBody body = DecompressAndDecode<BlockBody>(buffer, e);

            //item = valueDecoder.Decode(ref rlpValueContext, RlpBehaviors.AllowExtraBytes);


            (read, e) = await _store.ReadEntryAt(blockOffset, cancellationToken);
            CheckType(e, E2Store.TypeCompressedReceipts);
            blockOffset += read;

            read = Decompress(buffer, 0, e);
            TxReceipt[] receipts = DecodeReceipts(buffer, 0, read);
            //TxReceipt[] receipts = DecompressAndDecode<TxReceipt[]>(buffer, e);

            (read, e) = await _store.ReadEntryAt(blockOffset, cancellationToken);
            CheckType(e, E2Store.TypeTotalDifficulty);
            blockOffset += read;
           
            _currentTotalDiffulty = e.Value.AsRlpValueContext().DecodeUInt256();

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

    private async Task<long> FindBlockOffset(int blockIndex, CancellationToken token = default)
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
    private static int Decompress(byte[] buffer, int offset, Entry e)
    {
        using var decompressionStream = new SnappyStream(e.ValueAsStream(), System.IO.Compression.CompressionMode.Decompress);
        //TODO handle read more than buffer length
        var bufferRead = decompressionStream.Read(buffer, offset, buffer.Length - offset);
        return bufferRead;
    }

    private static T DecompressAndDecode<T>(byte[] buffer, Entry e)
    {
        using var decompressionStream = new SnappyStream(e.ValueAsStream(), System.IO.Compression.CompressionMode.Decompress);
        //TODO handle read more than buffer length
        var bufferRead = decompressionStream.Read(buffer, 0, buffer.Length);
        var section = new Span<byte>(buffer, 0, bufferRead);
        return RlpDecode<T>(section);
    }

    private static T RlpDecode<T>(Span<byte> buffer)
    {
        return Rlp.Decode<T>(buffer);
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
