// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Buffers;
using System.Text;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.LightClient.Verifier;

internal sealed class ProofRelayExecution(string[] relays, ISpecProvider specs) : IExecutionStateSource, IDisposable
{
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private int _requestId;

    public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken token) => TryRelaysAsync(async relay =>
    {
        using JsonDocument proof = await RequestProofAsync(relay, head, address, [], token);
        return ExecutionProofVerifier.VerifyAccount(head.StateRoot, address, ParseProof(proof.RootElement.GetProperty("accountProof")));
    }, token);

    public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken token)
    {
        if (!account.HasStorage) return Task.FromResult(UInt256.Zero);
        return TryRelaysAsync(async relay =>
        {
            string storageKey = key.ToHexString(skipLeadingZeros: true);
            using JsonDocument proof = await RequestProofAsync(relay, head, address, [storageKey], token);
            JsonElement response = proof.RootElement;
            Account owner = ExecutionProofVerifier.VerifyAccount(head.StateRoot, address, ParseProof(response.GetProperty("accountProof")));
            if (owner != account) throw new InvalidDataException("Storage proof account differs from the authenticated account.");
            JsonElement values = response.GetProperty("storageProof");
            if (values.GetArrayLength() != 1) throw new InvalidDataException("Storage response omitted the requested proof.");
            return ExecutionProofVerifier.VerifyStorage(account.StorageRoot, key, ParseProof(values[0].GetProperty("proof")));
        }, token);
    }

    public Task<byte[]> GetCodeAsync(Account account, CancellationToken token)
    {
        if (account.CodeHash == Keccak.OfAnEmptyString) return Task.FromResult(Array.Empty<byte>());
        return TryRelaysAsync(async relay =>
        {
            byte[] code = await GetBytesAsync($"{relay}/lightclient/code/{account.CodeHash}", 1024 * 1024, token);
            ExecutionProofVerifier.VerifyCode(account.CodeHash, code);
            return code;
        }, token);
    }

    public Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken token) => TryRelaysAsync(async relay =>
    {
        byte[] bytes = await GetBytesAsync($"{relay}/lightclient/header/{hash}", 2048, token);
        return DecodeHeader(bytes, hash, specs);
    }, token);

    public async Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken token)
    {
        BlockHeader header = await GetHeaderByHashAsync(head.BlockHash, token);
        if (header.Number != head.Number || header.StateRoot != head.StateRoot)
            throw new InvalidDataException("Execution header differs from the authenticated execution state.");
        return header;
    }

    public async Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken token)
    {
        if (firstNumber >= head.Number || head.Number - firstNumber > 256)
            throw new ArgumentOutOfRangeException(nameof(firstNumber));
        Hash256[] result = new Hash256[head.Number - firstNumber];
        BlockHeader header = await GetHeaderAsync(head, token);
        for (ulong number = head.Number; number > firstNumber; number--)
        {
            Hash256 parentHash = header.ParentHash!;
            result[number - firstNumber - 1] = parentHash;
            if (number - 1 == firstNumber) break;
            BlockHeader parent = await GetHeaderByHashAsync(parentHash, token);
            if (parent.Number != number - 1) throw new InvalidDataException("Execution headers are not consecutive.");
            header = parent;
        }
        return result;
    }

    internal static BlockHeader DecodeHeader(byte[] bytes, Hash256 expected, ISpecProvider specs)
    {
        if (bytes.Length is 0 or > 2048 || Keccak.Compute(bytes) != expected)
            throw new InvalidDataException("Execution header does not match its authenticated hash.");
        RlpReader reader = new(bytes);
        HeaderDecoder decoder = new(specs);
        BlockHeader header = decoder.Decode(ref reader)
            ?? throw new InvalidDataException("Execution header is missing.");
        if (!bytes.AsSpan().SequenceEqual(decoder.Encode(header).Bytes))
            throw new InvalidDataException("Execution header is not canonical RLP.");
        return header;
    }

    private async Task<JsonDocument> RequestProofAsync(string relay, VerifiedHead head, Address address, string[] keys, CancellationToken token)
    {
        int id = Interlocked.Increment(ref _requestId);
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", "eth_getProof");
            writer.WriteStartArray("params");
            writer.WriteStringValue(address.ToString());
            writer.WriteStartArray();
            foreach (string key in keys) writer.WriteStringValue(key);
            writer.WriteEndArray();
            writer.WriteStartObject();
            writer.WriteString("blockHash", head.BlockHash.ToString());
            writer.WriteBoolean("requireCanonical", true);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, relay);
        request.Content = new StringContent(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        byte[] bytes = await ReadBoundedAsync(response, 512 * 1024, token);
        using JsonDocument envelope = JsonDocument.Parse(bytes);
        JsonElement root = envelope.RootElement;
        if (root.GetProperty("jsonrpc").GetString() != "2.0" || root.GetProperty("id").GetInt32() != id)
            throw new InvalidDataException("Proof relay returned an unmatched JSON-RPC response.");
        if (root.TryGetProperty("error", out JsonElement error))
            throw new IOException(error.GetProperty("message").GetString());
        return JsonDocument.Parse(root.GetProperty("result").GetRawText());
    }

    private async Task<byte[]> GetBytesAsync(string url, int maximum, CancellationToken token)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url);
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        return await ReadBoundedAsync(response, maximum, token);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Proof relay response exceeds its size limit.");
        await using Stream input = await response.Content.ReadAsStreamAsync(token);
        using MemoryStream output = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(buffer, token);
                if (read == 0) return output.ToArray();
                if (output.Length + read > maximum) throw new InvalidDataException("Proof relay response exceeds its size limit.");
                output.Write(buffer, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        HttpRequestMessage request = new(method, url);
        request.Options.Set(new HttpRequestOptionsKey<bool>("WebAssemblyEnableStreamingResponse"), true);
        request.Options.Set(new HttpRequestOptionsKey<IDictionary<string, object>>("WebAssemblyFetchOptions"),
            new Dictionary<string, object> { ["credentials"] = "omit", ["redirect"] = "error" });
        return request;
    }

    private static byte[][] ParseProof(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > ExecutionProofVerifier.MaxProofNodes)
            throw new InvalidDataException("Execution proof has an invalid node count.");
        byte[][] proof = new byte[list.GetArrayLength()][];
        for (int i = 0; i < proof.Length; i++)
        {
            string text = list[i].GetString() ?? throw new InvalidDataException("Execution proof node is missing.");
            if (!text.StartsWith("0x", StringComparison.Ordinal) || text.Length > 2 + 2 * ExecutionProofVerifier.MaxNodeBytes)
                throw new InvalidDataException("Execution proof node exceeds its size limit.");
            proof[i] = Convert.FromHexString(text.AsSpan(2));
        }
        return proof;
    }

    private async Task<T> TryRelaysAsync<T>(Func<string, Task<T>> request, CancellationToken token)
    {
        Exception? failure = null;
        foreach (string relay in relays)
        {
            token.ThrowIfCancellationRequested();
            try { return await request(relay); }
            catch (Exception exception) when (!token.IsCancellationRequested && exception is (HttpRequestException or IOException or InvalidDataException
                or JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException or OperationCanceledException))
            { failure = exception; }
        }
        throw new IOException("No proof relay supplied authenticated execution data.", failure);
    }

    public void Dispose() => _client.Dispose();
}
