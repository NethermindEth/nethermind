// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Nethermind.Eez.Execution.Settlement;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Client;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Json;

namespace Nethermind.Eez.Follower.RollupX;

internal sealed class NativeL1Rpc(HttpClient http, IJsonSerializer serializer, NativeL1Rules rules)
{
    internal const int MaxResponseBytes = 4 * 1024 * 1024;
    internal NativeL1Rules Rules => rules;

    internal async Task<NativeL1Header> Read(ulong number, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(http.Timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(100) : http.Timeout);
        token = deadline.Token;
        using HttpRequestMessage message = new(HttpMethod.Post, "")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "eth_getBlockByNumber",
                @params = new object[] { number.ToHexString(true), false }
            })
        };
        using HttpResponseMessage response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaxResponseBytes, token);
        using Stream body = await response.Content.ReadAsStreamAsync(token);
        NativeL1Header header = ReadResponse(body, number);
        token.ThrowIfCancellationRequested();
        return header;
    }

    private NativeL1Header ReadResponse(Stream body, ulong number)
    {
        try
        {
            JsonRpcResponse<BlockHeaderForRpc?>? decoded = serializer.Deserialize<JsonRpcResponse<BlockHeaderForRpc?>>(body);
            if (decoded is null || decoded.Id is not 1L || decoded.JsonRpc != "2.0" || decoded.Error is not null || decoded.Result is null)
                throw new L1SourceIncompleteException(number, $"L1 refuses canonical header {number}.");
            return Decode(decoded.Result, number, rules.Headers);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or EezSettlementException)
        {
            throw new L1SourceIncompleteException(number, $"L1 returned an invalid canonical header at {number}.");
        }
    }

    internal static NativeL1Header Decode(BlockHeaderForRpc header, ulong number, in NativeL1HeaderRules rules)
    {
        if (header.Number != number || header.Hash is null || header.ParentHash is null || header.Sha3Uncles is null ||
            header.Miner is null || header.StateRoot is null || header.TransactionsRoot is null || header.ReceiptsRoot is null ||
            header.LogsBloom is null || header.MixHash is null || header.Nonce is null || header.Timestamp > ulong.MaxValue ||
            header.ExtraData is not { Length: <= 32 } || header.Signature is not null || header.Step is not null ||
            header.SlotNumber is not null || header.BlockAccessListHash is not null)
            throw NativeL1Header.Invalid("incomplete or unsupported canonical RPC header");

        Span<byte> buffer = stackalloc byte[NativeL1Header.MaxBytes];
        RlpWriter writer = new(buffer[3..]);
        writer.Encode(header.ParentHash);
        writer.Encode(header.Sha3Uncles);
        writer.Encode(header.Miner);
        writer.Encode(header.StateRoot);
        writer.Encode(header.TransactionsRoot);
        writer.Encode(header.ReceiptsRoot);
        writer.Encode(header.LogsBloom);
        writer.Encode(header.Difficulty);
        writer.Encode(number);
        writer.Encode(header.GasLimit);
        writer.Encode(header.GasUsed);
        writer.Encode((ulong)header.Timestamp);
        writer.Encode(header.ExtraData);
        writer.Encode(header.MixHash);
        Span<byte> nonce = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(nonce, header.Nonce.Value);
        writer.Encode(nonce);
        if (header.BaseFeePerGas is { } fee) writer.Encode(fee);
        if (header.WithdrawalsRoot is { } withdrawals) writer.Encode(withdrawals);
        if (header.BlobGasUsed is { } used) writer.Encode(used);
        if (header.ExcessBlobGas is { } excess) writer.Encode(excess);
        if (header.ParentBeaconBlockRoot is { } beacon) writer.Encode(beacon);
        if (header.RequestsHash is { } requests) writer.Encode(requests);
        int length = Rlp.LengthOfSequence(writer.Position);
        int prefix = length - writer.Position;
        RlpWriter outer = new(buffer.Slice(3 - prefix, prefix));
        outer.StartSequence(writer.Position);
        NativeL1Header decoded = NativeL1Header.Decode(buffer.Slice(3 - prefix, length), rules);
        if (decoded.Hash != header.Hash.ValueHash256) throw NativeL1Header.Invalid("canonical RPC hash differs from its header");
        return decoded;
    }
}
