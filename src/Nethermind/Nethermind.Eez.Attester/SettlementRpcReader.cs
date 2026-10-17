// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Core.Buffers;

namespace Nethermind.Eez.Attester;

internal sealed class SettlementRpcReader
{
    internal const int SmallResponseLimit = 64 * 1024;
    internal const int BlockResponseLimit = 2 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    internal SettlementRpcReader(HttpClient client, TimeSpan timeout, TimeProvider? time = null)
    {
        if (client.BaseAddress is not { IsAbsoluteUri: true, Scheme: "http" or "https" })
            throw new ArgumentException("RPC source requires an absolute HTTP address", nameof(client));
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _client = client;
        _timeout = timeout;
        _time = time ?? TimeProvider.System;
    }

    internal async Task<JsonElement> Read(string method, object[] parameters, CancellationToken cancellation,
        int maxBytes = SmallResponseLimit)
    {
        if (maxBytes <= 0 || maxBytes > BlockResponseLimit) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        using CancellationTokenSource deadline = new(_timeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, _client.BaseAddress);
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = 1, method, @params = parameters }));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maxBytes) throw Unavailable();
            using Stream stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using ArrayPoolDisposableReturn rented = ArrayPoolDisposableReturn.Rent(maxBytes + 1, out byte[] bytes);
            int length = 0;
            while (length <= maxBytes)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(length, maxBytes + 1 - length), linked.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            if (length > maxBytes) throw Unavailable();
            using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement envelope = document.RootElement;
            if (envelope.ValueKind != JsonValueKind.Object) throw Unavailable();
            int versionCount = 0, idCount = 0, resultCount = 0;
            foreach (JsonProperty property in envelope.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "jsonrpc": versionCount++; break;
                    case "id": idCount++; break;
                    case "result": resultCount++; break;
                    case "error": throw Unavailable();
                }
            }
            if (versionCount != 1 || idCount != 1 || resultCount != 1 ||
                envelope.GetProperty("jsonrpc").ValueKind != JsonValueKind.String || envelope.GetProperty("jsonrpc").GetString() != "2.0" ||
                !envelope.GetProperty("id").TryGetInt32(out int id) || id != 1 || envelope.GetProperty("result").ValueKind == JsonValueKind.Null)
                throw Unavailable();
            linked.Token.ThrowIfCancellationRequested();
            return envelope.GetProperty("result").Clone();
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw Unavailable(); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            throw Unavailable();
        }
    }

    internal static byte[] Bytes(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Unavailable();
        string hex = value.GetString()!;
        if (!hex.StartsWith("0x", StringComparison.Ordinal) || hex.Length % 2 != 0) throw Unavailable();
        try { return Convert.FromHexString(hex.AsSpan(2)); }
        catch (FormatException) { throw Unavailable(); }
    }

    internal static ulong Quantity(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Unavailable();
        string hex = value.GetString()!;
        if (!hex.StartsWith("0x", StringComparison.Ordinal) || hex.Length < 3 || (hex.Length > 3 && hex[2] == '0') ||
            !ulong.TryParse(hex.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong number))
            throw Unavailable();
        return number;
    }

    internal static RpcException Unavailable() => new(new Status(StatusCode.Unavailable, "settlement evidence unavailable"));
}
