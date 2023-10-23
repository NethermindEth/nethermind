// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Snappier;

namespace Nethermind.Era1;

internal class StreamSegment : Stream
{
    private readonly Stream _internalStream;
    private readonly long _streamOffset;
    private readonly long _streamLength;

    public StreamSegment(Stream stream, long offset, long length)
    {
        _internalStream = stream ?? throw new ArgumentNullException(nameof(stream));
        _streamOffset = offset;
        _streamLength = length;
        if (!stream.CanRead)
            throw new ArgumentException("Must be a readable stream.",nameof(stream));
        if (_internalStream.Length < _streamOffset + _streamLength)
            throw new ArgumentOutOfRangeException(nameof(length), "Will exceed the length of the stream.");
        _internalStream.Position = offset;
    }
    public override bool CanRead => _internalStream.CanRead;

    public override bool CanSeek => _internalStream.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _streamLength;
    public override long Position
    {
        get => _internalStream.Position + _streamOffset;
        set
        {
            //TODO boundary check
            if (value > Length)
                throw new ArgumentOutOfRangeException(nameof(value), "Value exceeds the length of the stream.");
            _internalStream.Position = value + _streamOffset;
        }
    }

    public override void Flush()
    {
        throw new NotImplementedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Cannot be less than zero.");
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "Cannot be less than zero.");
        if (count == 0)
            return 0;

        long actualCount = count + _internalStream.Position > _streamLength + _streamOffset ? _streamLength + _streamOffset - _internalStream.Position : count;
        return await _internalStream.ReadAsync(buffer, offset, (int)actualCount);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return _internalStream.Seek(offset + _streamOffset, origin);
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
