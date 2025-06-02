// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;

namespace Nethermind.Optimism.CL.P2P;

public class ReadOnlySequenceStream(ReadOnlySequence<byte> sequence) : Stream
{
    private SequencePosition _position = sequence.Start;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var reader = new SequenceReader<byte>(sequence.Slice(_position));

        int totalRead = 0;
        while (totalRead < count && reader.Remaining > 0)
        {
            var readableSpan = reader.CurrentSpan.Slice(reader.CurrentSpanIndex);
            int toCopy = Math.Min(count - totalRead, readableSpan.Length);
            readableSpan.Slice(0, toCopy).CopyTo(buffer.AsSpan(offset + totalRead));
            totalRead += toCopy;
            reader.Advance(toCopy);
        }
        _position = reader.Position;

        return totalRead;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => sequence.Length;

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
}
