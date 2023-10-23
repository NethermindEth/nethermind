// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Era1;
internal class StreamHelper : Stream
{
    private readonly Stream _internalStream;

    public StreamHelper(Stream stream)
    {
        _internalStream = stream;
    }
    public int LastWriteBytesWritten { get; private set; }

    public override bool CanRead => _internalStream.CanRead;

    public override bool CanSeek => _internalStream.CanSeek;

    public override bool CanWrite => _internalStream.CanWrite;

    public override long Length => _internalStream.Length;

    public override long Position { get => _internalStream.Position; set => _internalStream.Position = value; }

    public override void Flush()
    {
        _internalStream.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _internalStream.Read(buffer, offset, count);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return _internalStream.Seek(offset, origin);   
    }

    public override void SetLength(long value)
    {
        _internalStream.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _internalStream.Write(buffer, offset, count);
        LastWriteBytesWritten = count;
    }
}
