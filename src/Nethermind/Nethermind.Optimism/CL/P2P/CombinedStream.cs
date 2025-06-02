// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;

namespace Nethermind.Optimism.CL.P2P;

public class CombinedStream : Stream
{
    private readonly IEnumerator<Stream> _streams;
    private Stream? _currentStream;

    public CombinedStream(IEnumerator<Stream> streams)
    {
        _streams = streams;
        MoveNext();
    }

    private void MoveNext() => _currentStream = _streams.MoveNext() ? _streams.Current : null;

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (_currentStream is not null)
        {
            int read = _currentStream.Read(buffer, offset, count);
            if (read > 0) return read;
            MoveNext();
        }

        return 0;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
}
