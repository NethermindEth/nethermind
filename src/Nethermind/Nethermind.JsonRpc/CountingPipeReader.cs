// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Runner.JsonRpc;

public sealed class CountingPipeReader : PipeReader
{
    private readonly PipeReader _wrappedReader;
    private ReadOnlySequence<byte> _currentSequence;

    public long Length { get; private set; }

    public CountingPipeReader(PipeReader stream)
    {
        _wrappedReader = stream;
    }

    public override void AdvanceTo(SequencePosition consumed)
    {
        Length += _currentSequence.GetOffset(consumed);
        _wrappedReader.AdvanceTo(consumed);
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        Length += _currentSequence.GetOffset(consumed);
        _wrappedReader.AdvanceTo(consumed, examined);
    }

    public override void CancelPendingRead()
    {
        _wrappedReader.CancelPendingRead();
    }

    public override void Complete(Exception? exception = null)
    {
        Length += _currentSequence.Length;
        _wrappedReader.Complete(exception);
    }

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        ReadResult result = await _wrappedReader.ReadAsync(cancellationToken);
        _currentSequence = result.Buffer;
        return result;
    }

    public override bool TryRead(out ReadResult result)
    {
        bool didRead = _wrappedReader.TryRead(out result);
        if (didRead)
        {
            _currentSequence = result.Buffer;
        }

        return didRead;
    }
}
