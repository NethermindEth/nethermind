// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Network.Rlpx.Sockets;

namespace Nethermind.Network.Test.Rlpx.Sockets;

/// <summary>
/// One end of an in-memory byte stream, with the short sends, split reads, held sends and injected errors that a real
/// socket can't produce on demand.
/// </summary>
internal sealed class InMemoryRlpxSocket : IRlpxSocket
{
    private readonly Pipe _incoming;
    private readonly Pipe _outgoing;
    private Exception? _receiveError;
    private volatile bool _closed;
    private long _received;

    private InMemoryRlpxSocket(Pipe incoming, Pipe outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    /// <summary>Creates both ends of a stream.</summary>
    public static (InMemoryRlpxSocket Local, InMemoryRlpxSocket Remote) CreatePair()
    {
        Pipe toLocal = new(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        Pipe toRemote = new(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
        return (new InMemoryRlpxSocket(toLocal, toRemote), new InMemoryRlpxSocket(toRemote, toLocal));
    }

    /// <summary>Gets or sets the most bytes a receive returns, so that data arrives split.</summary>
    public int MaxReceiveLength { get; set; } = int.MaxValue;

    /// <summary>Gets or sets the most bytes a send takes, so that sends complete short.</summary>
    public int MaxSendLength { get; set; } = int.MaxValue;

    /// <summary>Gets or sets a gate that every send waits for.</summary>
    public Task SendGate { get; set; } = Task.CompletedTask;

    /// <summary>Gets or sets an error that sends throw.</summary>
    public Exception? SendError { get; set; }

    /// <summary>Gets the number of bytes received so far.</summary>
    public long Received => Interlocked.Read(ref _received);

    public bool IsClosed => _closed;

    /// <summary>Makes the pending and later receives throw, after the bytes already received.</summary>
    public void FailReceive(Exception error)
    {
        _receiveError = error;
        _incoming.Reader.CancelPendingRead();
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            ReadResult result = await _incoming.Reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> data = result.Buffer;
            if (_receiveError is { } error)
            {
                _incoming.Reader.AdvanceTo(data.Start);
                throw error;
            }

            if (result.IsCanceled)
            {
                _incoming.Reader.AdvanceTo(data.Start);
                continue;
            }

            if (buffer.IsEmpty || data.IsEmpty)
            {
                // A zero-byte receive waits for data without taking it; an empty completed stream is its end.
                _incoming.Reader.AdvanceTo(data.Start);
                return 0;
            }

            int length = (int)Math.Min(Math.Min(data.Length, buffer.Length), MaxReceiveLength);
            data.Slice(0, length).CopyTo(buffer.Span);
            _incoming.Reader.AdvanceTo(data.GetPosition(length));
            Interlocked.Add(ref _received, length);
            return length;
        }
    }

    public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        await SendGate.WaitAsync(cancellationToken);
        ObjectDisposedException.ThrowIf(_closed, this);
        if (SendError is { } error)
        {
            throw error;
        }

        int length = Math.Min(buffer.Length, MaxSendLength);
        await _outgoing.Writer.WriteAsync(buffer[..length], cancellationToken);
        return length;
    }

    /// <summary>Sends all of <paramref name="data"/>.</summary>
    public async Task SendAllAsync(ReadOnlyMemory<byte> data)
    {
        while (!data.IsEmpty)
        {
            int sent = await SendAsync(data, CancellationToken.None);
            data = data[sent..];
        }
    }

    /// <summary>Receives exactly <paramref name="length"/> bytes.</summary>
    public async Task<byte[]> ReceiveExactlyAsync(int length, CancellationToken cancellationToken)
    {
        byte[] data = new byte[length];
        int received = 0;
        while (received < length)
        {
            int read = await ReceiveAsync(data.AsMemory(received), cancellationToken);
            if (read == 0)
            {
                throw new InvalidOperationException($"The stream ended after {received} of {length} bytes.");
            }

            received += read;
        }

        return data;
    }

    /// <summary>Closes this end: the other end sees the end of the stream.</summary>
    public void Close()
    {
        _closed = true;
        _outgoing.Writer.Complete();
        _incoming.Reader.CancelPendingRead();
    }

    public void Dispose() => Close();
}
