// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Nethermind.Runner.Ethereum.WebTransport;

internal sealed class MsQuicListener : IAsyncDisposable
{
    private const int MaxConnections = 64;
    private const int MaxConnectionsPerAddress = 4;
    private readonly Channel<MsQuicConnection> _accepted = Channel.CreateBounded<MsQuicConnection>(MaxConnections);
    private readonly ConcurrentDictionary<nint, MsQuicConnection> _connections = new();
    private readonly Lock _configurationGate = new();
    private readonly Lock _disposeGate = new();
    private readonly MsQuicNative _native;
    private nint _registration;
    private nint _configuration;
    private nint _listener;
    private GCHandle _context;
    private Task? _disposal;
    private bool _stopping;

    private MsQuicListener(MsQuicNative native) => _native = native;

    internal IPEndPoint LocalEndPoint { get; private set; } = null!;

    internal static unsafe MsQuicListener Listen(IPEndPoint endpoint, X509Certificate2 certificate)
    {
        MsQuicListener server = new(new MsQuicNative());
        try
        {
            server._registration = server._native.OpenRegistration();
            server._configuration = server._native.OpenConfiguration(server._registration, certificate);
            server._context = GCHandle.Alloc(server);
            server._listener = server._native.OpenListener(
                server._registration,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, ListenerEvent*, int>)&OnEvent,
                GCHandle.ToIntPtr(server._context));
            server.LocalEndPoint = server._native.StartListener(server._listener, endpoint);
            return server;
        }
        catch
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    internal ValueTask<MsQuicConnection> AcceptConnectionAsync(CancellationToken cancellationToken) =>
        _accepted.Reader.ReadAsync(cancellationToken);

    internal void UpdateCertificate(X509Certificate2 certificate)
    {
        lock (_configurationGate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            nint next = _native.OpenConfiguration(_registration, certificate);
            nint previous = _configuration;
            _configuration = next;
            _native.CloseConfiguration(previous);
        }
    }

    internal bool QueueConnection(MsQuicConnection connection) => _accepted.Writer.TryWrite(connection);
    // MsQuic can reuse a handle before the managed owner finishes closing.
    internal void ConnectionClosed(nint handle, MsQuicConnection connection) => _connections.TryRemove(new(handle, connection));

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        lock (_configurationGate)
            _stopping = true;

        if (_listener != 0) _native.CloseListener(_listener);
        _accepted.Writer.TryComplete();

        try
        {
            Task[] closes = new Task[_connections.Count];
            int count = 0;
            foreach ((_, MsQuicConnection connection) in _connections)
                closes[count++] = connection.DisposeAsync().AsTask();
            await Task.WhenAll(closes.AsSpan(0, count).ToArray());
        }
        finally
        {
            if (_configuration != 0) _native.CloseConfiguration(_configuration);
            if (_registration != 0) _native.CloseRegistration(_registration);
            if (_context.IsAllocated) _context.Free();
            _native.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnEvent(nint listener, nint context, ListenerEvent* evt)
    {
        if (evt->Type != 0) return 0; // QUIC_LISTENER_EVENT_NEW_CONNECTION
        MsQuicListener owner = (MsQuicListener)GCHandle.FromIntPtr(context).Target!;
        try
        {
            return owner.Accept(evt->Connection, evt->Info->RemoteAddress->ToEndpoint());
        }
        catch (Exception)
        {
            return MsQuicNative.Failed;
        }
    }

    private int Accept(nint handle, IPEndPoint address)
    {
        lock (_configurationGate)
        {
            if (_stopping || _connections.Count >= MaxConnections)
                return MsQuicNative.Refused;

            if (address.Address.IsIPv4MappedToIPv6)
                address.Address = address.Address.MapToIPv4();

            int connectionsFromAddress = 0;
            foreach ((_, MsQuicConnection connection) in _connections)
            {
                if (connection.RemoteEndPoint.Address.Equals(address.Address) &&
                    ++connectionsFromAddress >= MaxConnectionsPerAddress)
                    return MsQuicNative.Refused;
            }

            int status = _native.ConfigureConnection(handle, _configuration);
            if (MsQuicNative.IsFailure(status)) return status;

            _connections[handle] = new MsQuicConnection(this, _native, handle, address);
            return 0;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private unsafe struct ListenerEvent
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal NewConnectionInfo* Info;
        [FieldOffset(16)] internal nint Connection;
    }

    [StructLayout(LayoutKind.Explicit)]
    private unsafe struct NewConnectionInfo
    {
        [FieldOffset(16)] internal MsQuicNative.SocketAddress* RemoteAddress;
    }
}
