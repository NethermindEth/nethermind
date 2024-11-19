// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DotNetty.Buffers;
using Microsoft.IO;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Snappier;
namespace Nethermind.Era1;

public class E2StoreWriter : IDisposable
{
    internal const int HeaderSize = 8;

    private readonly Stream _stream;
    private bool _disposedValue;
    private RecyclableMemoryStreamManager _memoryStreamManager = new();
    private readonly IncrementalHash _checksumCalculator = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long Position => _stream.Position;

    public E2StoreWriter(Stream stream)
    {
        _stream = stream;
    }

    public Task<int> WriteEntryAsSnappy(UInt16 type, Memory<byte> bytes, CancellationToken cancellation = default)
    {
        return WriteEntry(type, bytes, true, cancellation);
    }
    public Task<int> WriteEntry(UInt16 type, Memory<byte> bytes, CancellationToken cancellation = default)
    {
        return WriteEntry(type, bytes, false, cancellation);
    }

    private async Task<int> WriteEntry(UInt16 type, Memory<byte> bytes, bool asSnappy, CancellationToken cancellation = default)
    {
        using ArrayPoolList<byte> headerBuffer = new(HeaderSize);
        // See https://github.com/google/snappy/blob/main/framing_format.txt
        if (asSnappy && bytes.Length > 0)
        {
            using RecyclableMemoryStream bufferedStream = new RecyclableMemoryStream(_memoryStreamManager);
            using SnappyStream compressor = new(bufferedStream!, CompressionMode.Compress, true);

            await compressor!.WriteAsync(bytes, cancellation);
            await compressor.FlushAsync();

            bool canGetBuffer = bufferedStream!.TryGetBuffer(out ArraySegment<byte> arraySegment);
            Debug.Assert(canGetBuffer);

            bytes = arraySegment;
        }

        headerBuffer.AddRange(MemoryMarshal.Cast<ushort, byte>(MemoryMarshal.CreateSpan(ref type, 1)));
        int length = bytes.Length;
        headerBuffer.AddRange(MemoryMarshal.Cast<int, byte>(MemoryMarshal.CreateSpan(ref length, 1)));
        headerBuffer.Add(0);
        headerBuffer.Add(0);

        ReadOnlyMemory<byte> headerMemory = headerBuffer.AsReadOnlyMemory()[..HeaderSize];

        _checksumCalculator.AppendData(headerMemory.Span);
        _checksumCalculator.AppendData(bytes.Span);

        await _stream.WriteAsync(headerMemory, cancellation);
        if (length > 0)
        {
            await _stream.WriteAsync(bytes, cancellation);
        }

        return length + HeaderSize;
    }

    public Task Flush(CancellationToken cancellation = default)
    {
        return _stream.FlushAsync(cancellation);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _stream?.Dispose();
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

    public ValueHash256 FinalizeChecksum()
    {
        return new ValueHash256(_checksumCalculator.GetHashAndReset());
    }
}
