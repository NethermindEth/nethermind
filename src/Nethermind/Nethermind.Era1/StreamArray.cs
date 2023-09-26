// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Era1;

//TODO find a better solution 
internal class StreamArray : Stream
{
    private readonly byte[] _bytes;
    private long _position;

    public StreamArray(byte[] bytes)
    {
        _bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
    }
    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _bytes.Length;

    public override long Position { get => _position; set => _position = value; }

    public override void Flush()
    {
        throw new NotImplementedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Cannot be less than zero.");
        var read = 0;
        while(_position < _bytes.Length && read < count)
        {
            buffer[read + offset] = _bytes[_position]; 
            read++;
            _position++;
        }
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotImplementedException();
    }

    public override void SetLength(long value)
    {
        throw new NotImplementedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }
}
