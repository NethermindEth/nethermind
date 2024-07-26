// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;

namespace Nethermind.Sockets;

public class CounterStream : Stream
{
    private readonly Stream _stream;

    public CounterStream(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public long WrittenBytes { get; private set; }

    public override void Flush() => _stream.Flush();

    public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);

    public override void SetLength(long value) => _stream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        _stream.Write(buffer, offset, count);
        WrittenBytes += count;
    }

    public override bool CanRead
    {
        get => _stream.CanRead;
    }

    public override bool CanSeek
    {
        get => _stream.CanSeek;
    }

    public override bool CanWrite
    {
        get => _stream.CanWrite;
    }

    public override long Length
    {
        get => _stream.Length;
    }

    public override bool CanTimeout
    {
        get => _stream.CanTimeout;
    }

    public override long Position
    {
        get => _stream.Position;
        set => _stream.Position = value;
    }
}
