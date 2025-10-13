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
using Nethermind.Core.Resettables;
using Nethermind.Serialization.Rlp;
using Snappier;
namespace Nethermind.EraE;

public class E2StoreWriter : Era1.E2StoreWriter
{
    private readonly Stream _stream;

    public E2StoreWriter(Stream stream): base(stream) {}

    public async Task<int> WriteEntryAsSnappy(ushort type, Memory<byte> bytes, CancellationToken cancellation = default)
    {
        // See https://github.com/google/snappy/blob/main/framing_format.txt
        using RecyclableMemoryStream bufferedStream = RecyclableStream.GetStream(nameof(E2StoreWriter));
        using SnappyStream compressor = new(bufferedStream!, CompressionMode.Compress, true);

        await compressor!.WriteAsync(bytes, cancellation);
        await compressor.FlushAsync();

        bool canGetBuffer = bufferedStream!.TryGetBuffer(out ArraySegment<byte> arraySegment);
        Debug.Assert(canGetBuffer);

        return await WriteEntry(type, arraySegment, cancellation);
    }

    public async Task<int> WriteEntry(ushort type, Memory<byte> bytes, CancellationToken cancellation = default)
    {
        using ArrayPoolList<byte> headerBuffer = new(HeaderSize);

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

    public void Dispose() => _stream.Dispose();

    public ValueHash256 FinalizeChecksum()
    {
        return new ValueHash256(_checksumCalculator.GetHashAndReset());
    }
}
