// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Nethermind.Logging;

namespace Nethermind.Taiko.Tdx;

/// <summary>
/// Client for communicating with the tdxs daemon via Unix socket.
/// Protocol: JSON request/response over Unix socket.
/// </summary>
public class TdxsClient(ISurgeTdxConfig config, ILogManager logManager) : ITdxsClient
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    public byte[] Issue(byte[] userData, byte[] nonce)
    {
        var request = new
        {
            method = "issue",
            data = new
            {
                userData = Convert.ToHexString(userData).ToLowerInvariant(),
                nonce = Convert.ToHexString(nonce).ToLowerInvariant()
            }
        };

        JsonElement response = SendRequest(request);

        if (response.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null)
        {
            throw new TdxException($"Attestation service error: {error.GetString()}");
        }

        if (!response.TryGetProperty("data", out JsonElement data) ||
            !data.TryGetProperty("document", out JsonElement document))
        {
            throw new TdxException("Invalid response: missing document");
        }

        return Convert.FromHexString(document.GetString()!);
    }

    public TdxMetadata GetMetadata()
    {
        var request = new { method = "metadata", data = new { } };
        JsonElement response = SendRequest(request);

        if (response.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null)
        {
            throw new TdxException($"Attestation service error: {error.GetString()}");
        }

        if (!response.TryGetProperty("data", out JsonElement data))
        {
            throw new TdxException("Invalid response: missing data");
        }

        return new TdxMetadata
        {
            IssuerType = data.GetProperty("issuerType").GetString()!,
            Metadata = data.TryGetProperty("metadata", out JsonElement meta) ? meta.Clone() : null
        };
    }

    private JsonElement SendRequest(object request)
    {
        string socketPath = config.SocketPath;

        if (!File.Exists(socketPath))
            throw new TdxException($"TDX socket not found at {socketPath}");

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(socketPath));

        string requestJson = JsonSerializer.Serialize(request);
        socket.Send(Encoding.UTF8.GetBytes(requestJson));
        socket.Shutdown(SocketShutdown.Send);

        using var stream = new NetworkStream(socket, ownsSocket: false);
        return JsonSerializer.Deserialize<JsonElement>(stream);
    }
}

public class TdxException(string message, Exception? innerException = null)
    : Exception(message, innerException);
