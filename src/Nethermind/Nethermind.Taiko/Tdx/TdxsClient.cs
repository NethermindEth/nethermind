// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nethermind.Taiko.Tdx;

/// <summary>
/// Client for communicating with the tdxs daemon via Unix socket.
/// Protocol: JSON request/response over Unix socket.
/// </summary>
public class TdxsClient(ISurgeTdxConfig config) : ITdxsClient
{

    public byte[] Issue(byte[] userData, byte[] nonce)
    {
        JsonObject request = new()
        {
            ["method"] = "issue",
            ["data"] = new JsonObject
            {
                ["userData"] = Convert.ToHexString(userData).ToLowerInvariant(),
                ["nonce"] = Convert.ToHexString(nonce).ToLowerInvariant()
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
        JsonObject request = new() { ["method"] = "metadata", ["data"] = new JsonObject() };
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

    private JsonElement SendRequest(JsonObject request)
    {
        string socketPath = config.SocketPath;

        if (!File.Exists(socketPath))
            throw new TdxException($"TDX socket not found at {socketPath}");

        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(socketPath));

        using NetworkStream stream = new(socket, ownsSocket: false);

        string requestJson = request.ToJsonString();
        socket.Send(Encoding.UTF8.GetBytes(requestJson));
        socket.Shutdown(SocketShutdown.Send);

        return JsonSerializer.Deserialize(stream, TdxJsonContext.Default.JsonElement);
    }
}

public class TdxException(string message, Exception? innerException = null)
    : Exception(message, innerException);
