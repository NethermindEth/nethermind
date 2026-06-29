// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Net.Client;

// Generated from proto/rpcdb/rpcdb.proto (package "rpcdb" => C# namespace "Rpcdb").
using RpcDb = global::Rpcdb;

namespace Nethermind.Avalanche.Vm;

/// <summary>
/// Thin adapter over the AvalancheGo <c>rpcdb.Database</c> gRPC service that the engine exposes at
/// <c>InitializeRequest.db_server_addr</c>. The VM does not own its consensus-state on disk; instead it
/// reads and writes through this remote key/value store so that AvalancheGo can manage atomic block
/// commits and reverts.
/// </summary>
/// <remarks>
/// All keys/values are opaque byte strings. A miss is signalled by the server returning
/// <c>err == ERROR_NOT_FOUND</c> (generated as <c>Rpcdb.Error.NotFound</c>); any other non-zero
/// <c>err</c> is surfaced as an <see cref="RpcDatabaseException"/>. The adapter owns the underlying
/// <see cref="GrpcChannel"/> and disposes it.
/// </remarks>
public sealed class RpcDatabase : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly RpcDb.Database.DatabaseClient _client;

    private RpcDatabase(GrpcChannel channel)
    {
        _channel = channel;
        _client = new RpcDb.Database.DatabaseClient(channel);
    }

    /// <summary>Opens an insecure h2c gRPC connection to the rpcdb server at <paramref name="address"/>.</summary>
    /// <param name="address">Host:port of the engine-provided rpcdb server, e.g. <c>127.0.0.1:1234</c>.</param>
    public static RpcDatabase Connect(string address)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);

        GrpcChannel channel = GrpcChannel.ForAddress(
            EnsureScheme(address),
            new GrpcChannelOptions
            {
                Credentials = Grpc.Core.ChannelCredentials.Insecure,
                MaxReceiveMessageSize = null,
                MaxSendMessageSize = null,
            });

        return new RpcDatabase(channel);
    }

    /// <summary>Returns whether <paramref name="key"/> is present.</summary>
    public async Task<bool> HasAsync(ReadOnlyMemory<byte> key, CancellationToken token = default)
    {
        RpcDb.HasResponse response = await _client.HasAsync(
            new RpcDb.HasRequest { Key = ByteString.CopyFrom(key.Span) },
            cancellationToken: token);
        ThrowOnUnexpectedError(response.Err);
        return response.Has;
    }

    /// <summary>Returns the value for <paramref name="key"/>, or <c>null</c> if the key is absent.</summary>
    public async Task<byte[]?> GetAsync(ReadOnlyMemory<byte> key, CancellationToken token = default)
    {
        RpcDb.GetResponse response = await _client.GetAsync(
            new RpcDb.GetRequest { Key = ByteString.CopyFrom(key.Span) },
            cancellationToken: token);

        if (response.Err == RpcDb.Error.NotFound)
        {
            return null;
        }

        ThrowOnUnexpectedError(response.Err);
        return response.Value.ToByteArray();
    }

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>.</summary>
    public async Task PutAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, CancellationToken token = default)
    {
        RpcDb.PutResponse response = await _client.PutAsync(
            new RpcDb.PutRequest
            {
                Key = ByteString.CopyFrom(key.Span),
                Value = ByteString.CopyFrom(value.Span),
            },
            cancellationToken: token);
        ThrowOnUnexpectedError(response.Err);
    }

    /// <summary>Removes <paramref name="key"/> if present.</summary>
    public async Task DeleteAsync(ReadOnlyMemory<byte> key, CancellationToken token = default)
    {
        RpcDb.DeleteResponse response = await _client.DeleteAsync(
            new RpcDb.DeleteRequest { Key = ByteString.CopyFrom(key.Span) },
            cancellationToken: token);
        ThrowOnUnexpectedError(response.Err);
    }

    /// <summary>
    /// Applies a batch of puts and deletes atomically, mirroring AvalancheGo's expectation that a block's
    /// state mutations land in a single <c>WriteBatch</c> at acceptance time.
    /// </summary>
    public async Task WriteBatchAsync(
        IReadOnlyList<KeyValuePair<byte[], byte[]>> puts,
        IReadOnlyList<byte[]> deletes,
        CancellationToken token = default)
    {
        RpcDb.WriteBatchRequest request = new();

        for (int i = 0; i < puts.Count; i++)
        {
            KeyValuePair<byte[], byte[]> put = puts[i];
            request.Puts.Add(new RpcDb.PutRequest
            {
                Key = ByteString.CopyFrom(put.Key),
                Value = ByteString.CopyFrom(put.Value),
            });
        }

        for (int i = 0; i < deletes.Count; i++)
        {
            request.Deletes.Add(new RpcDb.DeleteRequest { Key = ByteString.CopyFrom(deletes[i]) });
        }

        RpcDb.WriteBatchResponse response = await _client.WriteBatchAsync(request, cancellationToken: token);
        ThrowOnUnexpectedError(response.Err);
    }

    private static void ThrowOnUnexpectedError(RpcDb.Error err)
    {
        if (err is RpcDb.Error.Unspecified)
        {
            return;
        }

        throw new RpcDatabaseException($"rpcdb returned error: {err}");
    }

    private static string EnsureScheme(string address) =>
        address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? address
            : "http://" + address;

    public async ValueTask DisposeAsync()
    {
        await _channel.ShutdownAsync().ConfigureAwait(false);
        _channel.Dispose();
    }
}

/// <summary>Raised when the remote rpcdb returns an error other than <c>ERROR_NOT_FOUND</c>.</summary>
public sealed class RpcDatabaseException(string message) : Exception(message);
