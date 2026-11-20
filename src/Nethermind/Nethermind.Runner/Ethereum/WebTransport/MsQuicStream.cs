// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Nethermind.Runner.Ethereum.WebTransport;

internal sealed class MsQuicStream : Stream
{
    private readonly MsQuicNative _native;
    private readonly MsQuicConnection _owner;
    private readonly bool _local;
    private readonly Lock _nativeGate = new();
    private readonly Lock _disposeGate = new();
    private readonly Channel<byte[]> _received = Channel.CreateBounded<byte[]>(1);
    private readonly TaskCompletionSource<(int Status, long Id)> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _writesClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _handle;
    private GCHandle _context;
    private byte[]? _pending;
    private int _pendingOffset;
    private bool _writesCompleted;
    private Task? _disposal;

    private MsQuicStream(MsQuicNative native, MsQuicConnection owner, bool unidirectional, bool local)
    {
        _native = native;
        _owner = owner;
        _local = local;
        IsUnidirectional = unidirectional;
    }

    internal nint Handle => _handle;
    internal long Id { get; private set; }
    internal bool IsUnidirectional { get; }
    public override bool CanRead => !IsUnidirectional || !_local;
    public override bool CanWrite => !IsUnidirectional || _local;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    internal static unsafe MsQuicStream Open(MsQuicNative native, MsQuicConnection owner, nint connection, bool unidirectional)
    {
        MsQuicStream stream = new(native, owner, unidirectional, local: true);
        stream._context = GCHandle.Alloc(stream);
        try
        {
            stream._handle = native.OpenStream(connection, unidirectional, Callback, GCHandle.ToIntPtr(stream._context));
            native.StartStream(stream._handle);
            return stream;
        }
        catch
        {
            if (stream._handle != 0) native.CloseStream(stream._handle);
            stream.FreeContext();
            throw;
        }
    }

    private void FreeContext() => _context.Free();

    internal static MsQuicStream Accept(MsQuicNative native, MsQuicConnection owner, nint handle, bool unidirectional)
    {
        MsQuicStream stream = new(native, owner, unidirectional, local: false)
        {
            _handle = handle,
            Id = native.GetStreamId(handle),
        };
        stream._context = GCHandle.Alloc(stream);
        native.SetCallback(handle, Callback, GCHandle.ToIntPtr(stream._context));
        return stream;
    }

    internal async Task WaitForStartAsync(CancellationToken cancellationToken)
    {
        (int status, long id) = await _started.Task.WaitAsync(cancellationToken);
        MsQuicNative.Check(status, "starting a QUIC stream");
        Id = id;
    }

    private static unsafe nint Callback => (nint)(delegate* unmanaged[Cdecl]<nint, nint, StreamEvent*, int>)&OnEvent;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!CanRead) throw new NotSupportedException();
        if (buffer.IsEmpty) return 0;

        if (_pending is null)
        {
            if (!await _received.Reader.WaitToReadAsync(cancellationToken) ||
                !_received.Reader.TryRead(out _pending)) return 0;
            _pendingOffset = 0;
        }

        int count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsMemory(_pendingOffset, count).CopyTo(buffer);
        _pendingOffset += count;
        if (_pendingOffset == _pending.Length)
        {
            int length = _pending.Length;
            _pending = null;
            lock (_nativeGate)
            {
                ObjectDisposedException.ThrowIf(_handle == 0, this);
                _native.CompleteReceive(_handle, length);
            }
        }
        return count;
    }

    public override unsafe ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!CanWrite) throw new NotSupportedException();
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty) return ValueTask.CompletedTask;

        lock (_nativeGate)
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            if (_writesCompleted) throw new IOException("QUIC stream writes are closed.");
            SendOperation send = new(buffer);
            int status = _native.Send(_handle, send.Buffer, send.Context);
            if (MsQuicNative.IsFailure(status))
            {
                send.Dispose();
                throw MsQuicNative.Error(status, "sending on a QUIC stream");
            }
            return WaitForSendAsync(send.Completion.Task, cancellationToken);
        }
    }

    private static async ValueTask WaitForSendAsync(Task<bool> completion, CancellationToken cancellationToken)
    {
        if (!await completion.WaitAsync(cancellationToken))
            throw new IOException("QUIC stream send was canceled.");
    }

    internal void CompleteWrites()
    {
        lock (_nativeGate)
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            if (_writesCompleted) return;
            MsQuicNative.Check(_native.ShutdownStream(_handle, graceful: true), "completing QUIC stream writes");
            _writesCompleted = true;
        }
    }

    public override ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposal ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        bool graceful;
        lock (_nativeGate)
            graceful = _writesCompleted;

        if (graceful)
        {
            try
            {
                await _writesClosed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                // A peer that stops acknowledging the stream must not prevent teardown.
            }
        }

        nint handle;
        int status;
        lock (_nativeGate)
        {
            handle = _handle;
            _handle = 0;
            status = _native.ShutdownStream(handle, graceful: false);
        }

        if (!MsQuicNative.IsFailure(status))
            await _shutdown.Task;

        _native.CloseStream(handle);
        _context.Free();
        _owner.StreamClosed(handle, this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnEvent(nint stream, nint context, StreamEvent* evt)
    {
        MsQuicStream owner = (MsQuicStream)GCHandle.FromIntPtr(context).Target!;
        try
        {
            switch (evt->Type)
            {
                case 0: // START_COMPLETE
                    owner._started.TrySetResult((evt->Status, evt->StreamId));
                    break;
                case 1: // RECEIVE
                    return owner.Receive(evt);
                case 2: // SEND_COMPLETE
                    SendOperation send = (SendOperation)GCHandle.FromIntPtr(evt->SendContext).Target!;
                    send.Dispose();
                    send.Completion.TrySetResult(evt->Cancelled == 0);
                    break;
                case 3: // PEER_SEND_SHUTDOWN
                    owner._received.Writer.TryComplete();
                    break;
                case 4: // PEER_SEND_ABORTED
                    owner._received.Writer.TryComplete(new IOException("QUIC peer aborted the stream."));
                    break;
                case 6: // SEND_SHUTDOWN_COMPLETE
                    owner._writesClosed.TrySetResult();
                    break;
                case 7: // SHUTDOWN_COMPLETE
                    owner._received.Writer.TryComplete();
                    owner._writesClosed.TrySetResult();
                    owner._shutdown.TrySetResult();
                    break;
            }
            return 0;
        }
        catch (Exception)
        {
            return MsQuicNative.Failed;
        }
    }

    private unsafe int Receive(StreamEvent* evt)
    {
        if (evt->TotalBufferLength == 0) return 0;
        byte[] data = GC.AllocateUninitializedArray<byte>(checked((int)evt->TotalBufferLength));
        int offset = 0;
        for (uint i = 0; i < evt->BufferCount; i++)
        {
            MsQuicNative.Buffer source = evt->Buffers[i];
            new ReadOnlySpan<byte>(source.Data, checked((int)source.Length)).CopyTo(data.AsSpan(offset));
            offset += (int)source.Length;
        }
        // Leaving the native receive pending prevents MsQuic from granting stream credit until
        // the managed reader consumes this batch. MsQuic delivers one pending batch at a time.
        return _received.Writer.TryWrite(data) ? MsQuicNative.Pending : MsQuicNative.Failed;
    }

    [StructLayout(LayoutKind.Explicit)]
    private unsafe struct StreamEvent
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal int Status;
        [FieldOffset(8)] internal byte Cancelled;
        [FieldOffset(16)] internal long StreamId;
        [FieldOffset(16)] internal ulong TotalBufferLength;
        [FieldOffset(16)] internal nint SendContext;
        [FieldOffset(24)] internal MsQuicNative.Buffer* Buffers;
        [FieldOffset(32)] internal uint BufferCount;
    }

    private sealed unsafe class SendOperation : IDisposable
    {
        private GCHandle _context;
        internal readonly MsQuicNative.Buffer* Buffer;
        internal nint Context => GCHandle.ToIntPtr(_context);
        internal readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal SendOperation(ReadOnlyMemory<byte> data)
        {
            // WriteAsync cancellation lets the caller reuse its buffer while the native send completes.
            Buffer = (MsQuicNative.Buffer*)NativeMemory.Alloc((nuint)sizeof(MsQuicNative.Buffer) + (nuint)data.Length);
            try
            {
                byte* storage = (byte*)(Buffer + 1);
                data.Span.CopyTo(new Span<byte>(storage, data.Length));
                *Buffer = new() { Length = (uint)data.Length, Data = storage };
                _context = GCHandle.Alloc(this);
            }
            catch
            {
                NativeMemory.Free(Buffer);
                throw;
            }
        }

        public void Dispose()
        {
            NativeMemory.Free(Buffer);
            _context.Free();
        }
    }
}
