// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Follower.RollupX;
using Nethermind.Facade.Eth;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    [Test]
    public void NativeL1Rpc_CanonicalLayouts_PreservesExactHeader([Values(15, 16, 17, 20, 21)] int fields,
        [Values] bool zeroBaseFee)
    {
        byte[] raw = L1HeaderBytes(99, 120, default, fields, 15, Rlp.Encode(zeroBaseFee ? 0UL : 1UL));
        BlockHeaderForRpc rpc = RpcHeader(raw, fields);
        NativeL1Header result = NativeL1Rpc.Decode(rpc, 99, LayoutRules(fields));
        Assert.That(result.Encoded.ToArray(), Is.EqualTo(raw), "canonical RPC fields must preserve the exact header hash and bytes");
    }

    [Test]
    public void NativeL1Rpc_InvalidFields_Rejects([Values("hash", "number", "parent", "uncles", "miner", "state", "transactions",
        "receipts", "bloom", "randao", "nonce", "extra", "seal", "slot", "layout", "blob-gap")] string field)
    {
        BlockHeaderForRpc rpc = RpcHeader(L1HeaderBytes(99, 120, default, 21), 21);
        switch (field)
        {
            case "hash": rpc.Hash = Keccak.Zero; break;
            case "number": rpc.Number = 100; break;
            case "parent": rpc.ParentHash = null; break;
            case "uncles": rpc.Sha3Uncles = null; break;
            case "miner": rpc.Miner = null; break;
            case "state": rpc.StateRoot = null; break;
            case "transactions": rpc.TransactionsRoot = null; break;
            case "receipts": rpc.ReceiptsRoot = null; break;
            case "bloom": rpc.LogsBloom = null; break;
            case "randao": rpc.MixHash = null; break;
            case "nonce": rpc.Nonce = null; break;
            case "extra": rpc.ExtraData = new byte[33]; break;
            case "seal": rpc.Signature = []; break;
            case "slot": rpc.SlotNumber = 0; break;
            case "layout": rpc.RequestsHash = null; break;
            case "blob-gap": rpc.BlobGasUsed = null; break;
        }
        Assert.That(() => NativeL1Rpc.Decode(rpc, 99, LayoutRules(21)), Throws.TypeOf<EezSettlementException>(),
            "incomplete or contradictory RPC data cannot authenticate a native history origin");
    }

    [Test]
    public async Task NativeCanonicalHistory_CompleteCanonicalRange_PublishesOwnedSnapshot()
    {
        using NativeHistoryTestSource fixture = CreateHistorySource();
        Assert.That(() => fixture.History.Read(), Throws.TypeOf<L1SourceIncompleteException>(), "history starts unavailable");
        NativeL1HeaderChain result = await fixture.History.Prepare(0, fixture.Anchor(3), CancellationToken.None);
        fixture.Headers[1].MixHash = Keccak.Zero;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Count, Is.EqualTo(4));
            Assert.That(fixture.History.Read(), Is.SameAs(result));
            Assert.That(result.At(1).PrevRandao, Is.Not.EqualTo(default(ValueHash256)), "published history owns its checked bytes");
            Assert.That(fixture.Methods, Has.All.EqualTo("eth_getBlockByNumber"), "history requires only standard public RPC");
        }
    }

    [Test]
    public async Task NativeCanonicalHistory_CanonicalExtension_ReusesHeadersWithoutMutatingBorrowedRoot()
    {
        using NativeHistoryTestSource fixture = CreateHistorySource();
        NativeL1HeaderChain before = await fixture.History.Prepare(0, fixture.Anchor(2), CancellationToken.None);
        NativeL1HeaderChain after = await fixture.History.Prepare(1, fixture.Anchor(5), CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(before.Count, Is.EqualTo(3), "an in-flight executor retains its immutable snapshot");
            Assert.That(after.Count, Is.EqualTo(6), "a linked retained prefix can support other block execution scopes");
            Assert.That(after.At(1), Is.SameAs(before.At(1)), "checked header bodies do not need to be copied or decoded again");
            Assert.That(after.Endpoint.Number, Is.EqualTo(5UL));
        }
    }

    [Test]
    public async Task NativeCanonicalHistory_BadSegment_DoesNotPublishPartialHistory([Values("missing", "parent", "anchor", "genesis")] string failure)
    {
        using NativeHistoryTestSource fixture = CreateHistorySource();
        L1Anchor through = fixture.Anchor(4);
        if (failure == "missing") fixture.Headers.Remove(3);
        if (failure == "parent") fixture.Headers[3] = RpcHeader(L1HeaderBytes(3, (ulong)fixture.Headers[3].Timestamp, default, 21), 21);
        if (failure == "anchor") through = through with { Hash = Keccak.Zero };
        if (failure == "genesis") fixture.Headers[0] = RpcHeader(L1HeaderBytes(0, 1, default, 21), 21);
        await Assert.CatchAsync(async () => await fixture.History.Prepare(failure == "genesis" ? 1UL : 0UL, through, CancellationToken.None));
        Assert.That(() => fixture.History.Read(), Throws.TypeOf<L1SourceIncompleteException>(), "partial or wrong-branch evidence is never ready");
    }

    [Test]
    public async Task NativeCanonicalHistory_EndpointReorg_InvalidatesNewReadersButPreservesBorrowedRoot()
    {
        using NativeHistoryTestSource fixture = CreateHistorySource();
        NativeL1HeaderChain borrowed = await fixture.History.Prepare(0, fixture.Anchor(2), CancellationToken.None);
        int endpointReads = 0;
        fixture.OnRead = number =>
        {
            if (number == 4 && ++endpointReads == 2)
                fixture.Headers[4] = RpcHeader(L1HeaderBytes(4, (ulong)fixture.Headers[4].Timestamp, default, 21), 21);
        };
        await Assert.ThrowsAsync<L1SourceIncompleteException>(async () => await fixture.History.Prepare(0, fixture.Anchor(4), CancellationToken.None));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => fixture.History.Read(), Throws.TypeOf<L1SourceIncompleteException>());
            Assert.That(borrowed.Endpoint.Number, Is.EqualTo(2UL), "retired snapshots stay intact for existing execution scopes");
        }
    }

    [Test]
    public async Task NativeCanonicalHistory_InvalidationDuringRead_FencesPublication()
    {
        using NativeHistoryTestSource fixture = CreateHistorySource();
        fixture.OnRead = number => { if (number == 3) fixture.History.Invalidate(); };
        await Assert.ThrowsAsync<L1SourceIncompleteException>(async () => await fixture.History.Prepare(0, fixture.Anchor(4), CancellationToken.None));
        Assert.That(() => fixture.History.Read(), Throws.TypeOf<L1SourceIncompleteException>(), "a stale preparation cannot republish invalidated evidence");
    }

    [Test]
    public async Task NativeCanonicalHistory_CanceledRead_DoesNotPublish()
    {
        using NativeHistoryTestSource fixture = CreateHistorySource();
        using CancellationTokenSource cancellation = new();
        fixture.OnRead = number => { if (number == 2) cancellation.Cancel(); };
        await Assert.CatchAsync<OperationCanceledException>(async () => await fixture.History.Prepare(0, fixture.Anchor(4), cancellation.Token));
        Assert.That(() => fixture.History.Read(), Throws.TypeOf<L1SourceIncompleteException>());
    }

    [Test]
    public async Task NativeCanonicalHistory_OutOfRange_RejectsBeforeRpc([Values("reversed", "reserved", "oversized")] string failure)
    {
        using NativeHistoryTestSource fixture = CreateHistorySource();
        ulong first = failure == "reversed" ? 4UL : 0UL;
        L1Anchor through = fixture.Anchor(3) with { Number = failure == "reserved" ? ulong.MaxValue : failure == "oversized" ? 131072UL : 3UL };
        await Assert.ThrowsAsync<L1SourceIncompleteException>(async () => await fixture.History.Prepare(first, through, CancellationToken.None));
        Assert.That(fixture.Methods, Is.Empty, "invalid ranges cannot trigger unbounded RPC reads");
    }

    [Test]
    public async Task NativeCanonicalHistory_AncestryChangesAfterBoundaryRead_InvalidatesReadyHistory()
    {
        using NativeHistoryTestSource fixture = CreateHistorySource();
        NativeL1HeaderChain borrowed = await fixture.History.Prepare(0, fixture.Anchor(2), CancellationToken.None);
        fixture.OnRead = number =>
        {
            if (number == 3)
                fixture.Headers[3] = RpcHeader(L1HeaderBytes(3, (ulong)fixture.Headers[3].Timestamp, default, 21), 21);
        };
        await Assert.ThrowsAsync<EezSettlementException>(async () => await fixture.History.Prepare(0, fixture.Anchor(4), CancellationToken.None));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => fixture.History.Read(), Throws.TypeOf<L1SourceIncompleteException>(), "contradictory ancestry retires the ready branch");
            Assert.That(borrowed.Endpoint.Number, Is.EqualTo(2UL));
        }
    }

    [Test]
    public async Task NativeCanonicalHistory_HistoricalReplay_KeepsTheReadySuperset()
    {
        using NativeHistoryTestSource fixture = CreateHistorySource();
        NativeL1HeaderChain latest = await fixture.History.Prepare(0, fixture.Anchor(5), CancellationToken.None);
        NativeL1HeaderChain historical = await fixture.History.Prepare(1, fixture.Anchor(2), CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(historical.Endpoint.Number, Is.EqualTo(2UL), "the caller receives the exact older anchor");
            Assert.That(fixture.History.Read(), Is.SameAs(latest), "historical replay must not discard checked descendants");
        }
    }

    [Test]
    public async Task NativeCanonicalHistory_CanceledHttpRequest_ReleasesOwnershipAfterTransportStops()
    {
        using NativeHistoryTestSource fixture = CreateHistorySource();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool active = false;
        fixture.WaitOnRead = async (_, token) =>
        {
            active = true;
            entered.SetResult();
            try { await blocked.Task.WaitAsync(token); }
            finally { active = false; }
        };
        Task<NativeL1HeaderChain> preparation = fixture.History.Prepare(0, fixture.Anchor(4), cancellation.Token);
        await entered.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.History.Prepare(0, fixture.Anchor(4), CancellationToken.None));
        cancellation.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(async () => await preparation);
        Assert.That(active, Is.False, "the real transport token must stop the request before another preparation can start");
        fixture.WaitOnRead = null;
        await fixture.History.Prepare(0, fixture.Anchor(4), CancellationToken.None);
    }

    [Test]
    public async Task NativeL1Rpc_OversizedResponse_RejectsBeforeJsonParsing([Values] bool declaredLength)
    {
        using NativeHistoryTestSource fixture = CreateHistorySource();
        fixture.Response = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = declaredLength
                ? new ByteArrayContent(new byte[NativeL1Rpc.MaxResponseBytes + 1])
                : new UnknownLengthContent(NativeL1Rpc.MaxResponseBytes + 1)
        };
        await Assert.ThrowsAsync<HttpRequestException>(async () => await fixture.History.Prepare(0, fixture.Anchor(3), CancellationToken.None));
        Assert.That(() => fixture.History.Read(), Throws.TypeOf<L1SourceIncompleteException>());
    }

    private static BlockHeaderForRpc RpcHeader(byte[] encoded, int fields)
    {
        BlockHeader header = Rlp.Decode<BlockHeader>(encoded)!;
        header.Hash = Keccak.Compute(encoded);
        return new BlockHeaderForRpc(header)
        {
            BaseFeePerGas = fields >= 16 ? header.BaseFeePerGas : null,
            BlobGasUsed = fields >= 20 ? header.BlobGasUsed : null,
            ExcessBlobGas = fields >= 20 ? header.ExcessBlobGas : null,
            ParentBeaconBlockRoot = fields >= 20 ? header.ParentBeaconBlockRoot : null
        };
    }

    private static NativeHistoryTestSource CreateHistorySource()
    {
        byte[] bytes = L1Bytes(L1Document());
        NativeL1Rules l1 = NativeL1Rules.LoadEthereum(bytes);
        using MemoryStream input = new(bytes);
        ChainSpec spec = new GethGenesisLoader(new EthereumJsonSerializer()).Load(input);
        BlockHeader genesis = spec.Genesis!.Header;
        genesis.Hash = EezGenesisHash.Compute(spec);
        Dictionary<ulong, BlockHeaderForRpc> headers = new() { [0] = new(genesis, l1.Specs) };
        for (ulong number = 1; number <= 6; number++)
            headers[number] = RpcHeader(L1HeaderBytes(number, genesis.Timestamp + number * 12, headers[number - 1].Hash!.ValueHash256, 21), 21);
        RollupXSchedule schedule = new(2, 6, new RollupXL1Reference(1337, 0, genesis.Hash, genesis.Timestamp, genesis.MixHash!), genesis.Timestamp + 12);
        NativeHistoryTestSource fixture = new(headers);
        HttpClient http = new(fixture, disposeHandler: false) { BaseAddress = new Uri("http://l1.test/") };
        fixture.Http = http;
        fixture.History = new NativeCanonicalHistory(new NativeL1Rpc(http, new EthereumJsonSerializer(), l1),
            EezChainRules.NativeRollupX(schedule, Address.FromNumber(123), NativeSystemTxFormat.ContractV4));
        return fixture;
    }

    private sealed class NativeHistoryTestSource(Dictionary<ulong, BlockHeaderForRpc> headers) : HttpMessageHandler
    {
        internal Dictionary<ulong, BlockHeaderForRpc> Headers => headers;
        internal NativeCanonicalHistory History { get; set; } = null!;
        internal HttpClient Http { get; set; } = null!;
        internal List<string> Methods { get; } = [];
        internal Action<ulong>? OnRead { get; set; }
        internal Func<ulong, CancellationToken, Task>? WaitOnRead { get; set; }
        internal Func<HttpResponseMessage>? Response { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Methods.Add(body.RootElement.GetProperty("method").GetString()!);
            string value = body.RootElement.GetProperty("params")[0].GetString()!;
            ulong number = ulong.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            OnRead?.Invoke(number);
            if (WaitOnRead is { } wait) await wait(number, token);
            token.ThrowIfCancellationRequested();
            if (Response is { } response) return response();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new EthereumJsonSerializer().Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    result = headers.GetValueOrDefault(number)
                }))
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Http.Dispose();
            base.Dispose(disposing);
        }

        internal L1Anchor Anchor(ulong number)
        {
            BlockHeaderForRpc header = headers[number];
            return new L1Anchor(number, header.Hash!, (ulong)header.Timestamp, header.MixHash!);
        }
    }
    private sealed class UnknownLengthContent(int length) : HttpContent
    {
        protected override bool TryComputeLength(out long contentLength)
        {
            contentLength = 0;
            return false;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            byte[] chunk = new byte[4096];
            for (int offset = 0; offset < length; offset += chunk.Length)
                await stream.WriteAsync(chunk.AsMemory(0, Math.Min(chunk.Length, length - offset)));
        }
    }

}
