// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Nethermind.Runner.Ethereum.WebTransport;

internal sealed class MsQuicConnection : IAsyncDisposable
{
    private const int MaxQueuedStreams = 8;
    private readonly MsQuicListener _owner;
    private readonly MsQuicNative _native;
    private readonly nint _handle;
    private readonly Channel<MsQuicStream> _accepted = Channel.CreateBounded<MsQuicStream>(MaxQueuedStreams);
    private readonly ConcurrentDictionary<nint, MsQuicStream> _streams = new();
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _disposeGate = new();
    private GCHandle _context;
    private Task? _disposal;
    private bool _connected;

    internal unsafe MsQuicConnection(MsQuicListener owner, MsQuicNative native, nint handle, IPEndPoint remoteEndPoint)
    {
        _owner = owner;
        _native = native;
        _handle = handle;
        RemoteEndPoint = remoteEndPoint;
        _context = GCHandle.Alloc(this);
        _native.SetCallback(handle, (nint)(delegate* unmanaged[Cdecl]<nint, nint, ConnectionEvent*, int>)&OnEvent, GCHandle.ToIntPtr(_context));
    }

    internal IPEndPoint RemoteEndPoint { get; }
    internal ValueTask<MsQuicStream> AcceptStreamAsync(CancellationToken cancellationToken) => _accepted.Reader.ReadAsync(cancellationToken);

    internal async ValueTask<MsQuicStream> OpenStreamAsync(bool unidirectional, CancellationToken cancellationToken)
    {
        MsQuicStream stream;
        lock (_disposeGate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            stream = MsQuicStream.Open(_native, this, _handle, unidirectional);
            _streams[stream.Handle] = stream;
        }

        try
        {
            await stream.WaitForStartAsync(cancellationToken);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    internal void StreamClosed(nint handle, MsQuicStream stream) => _streams.TryRemove(new(handle, stream));

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private void ScheduleDisposal() => _ = Task.Run(async () => await DisposeAsync());

    private async Task DisposeCoreAsync()
    {
        _native.ShutdownConnection(_handle);
        await _shutdown.Task;
        try
        {
            foreach ((_, MsQuicStream stream) in _streams)
                await stream.DisposeAsync();
        }
        finally
        {
            _native.CloseConnection(_handle);
            _context.Free();
            _owner.ConnectionClosed(_handle, this);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnEvent(nint connection, nint context, ConnectionEvent* evt)
    {
        MsQuicConnection owner = (MsQuicConnection)GCHandle.FromIntPtr(context).Target!;
        try
        {
            switch (evt->Type)
            {
                case 0: // CONNECTED
                    owner._connected = true;
                    if (!owner._owner.QueueConnection(owner))
                        owner.ScheduleDisposal();
                    break;
                case 3: // SHUTDOWN_COMPLETE
                    owner._accepted.Writer.TryComplete(new IOException("QUIC connection closed."));
                    owner._shutdown.TrySetResult();
                    if (!owner._connected)
                        owner.ScheduleDisposal();
                    break;
                case 6: // PEER_STREAM_STARTED
                    evt->Flags |= 0x0004; // DELAY_ID_FC_UPDATES keeps the stream counted until StreamClose.
                    owner.AcceptStream(evt->Stream, (evt->Flags & 1) != 0);
                    break;
            }
            return 0;
        }
        catch (Exception)
        {
            return MsQuicNative.Failed;
        }
    }

    private void AcceptStream(nint handle, bool unidirectional)
    {
        MsQuicStream stream = MsQuicStream.Accept(_native, this, handle, unidirectional);
        _streams[handle] = stream;
        if (!_accepted.Writer.TryWrite(stream))
            _ = Task.Run(async () => await stream.DisposeAsync());
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct ConnectionEvent
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal nint Stream;
        [FieldOffset(16)] internal uint Flags;
    }
}
