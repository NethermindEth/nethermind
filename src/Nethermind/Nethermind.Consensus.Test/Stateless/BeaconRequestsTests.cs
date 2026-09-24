// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.StatelessInputGen;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class BeaconRequestsTests
{
    private static readonly byte[] DepositPubkey = Fill(48, 0x10);
    private static readonly byte[] DepositCredentials = Fill(32, 0x40);
    private static readonly byte[] DepositSignature = Fill(96, 0x60);
    private const ulong DepositAmount = 32_000_000_000;
    private const ulong DepositIndex = 1_234_567;

    private static readonly byte[] WithdrawalSource = Fill(20, 0xa0);
    private static readonly byte[] WithdrawalPubkey = Fill(48, 0xb0);
    private const ulong WithdrawalAmount = 1_000_000_007;

    // First consolidation of mainnet block 26050125 (beacon slot 15288570).
    private static readonly byte[] ConsolidationSource = Convert.FromHexString("b9d7934878b5fb9610b3fe8a5e441e8fad7e293f");
    private static readonly byte[] ConsolidationSourcePubkey = Convert.FromHexString("9858ba9c347ba52d597d1b883842578f0d22a69ebf4bf409e4e14f1aa3ea80751f5b5be6309aa7289eafcb44789910c2");
    private static readonly byte[] ConsolidationTargetPubkey = Convert.FromHexString("83f30003c5b2d460188dfd7de28054e32d5b783a305e48b2ea80ff7cc8cbfe743f2ab5f02d4cd591260b453e3d3fdd79");

    [Test]
    public void Encodes_all_request_types_to_flat_bytes()
    {
        byte[][] expected = [ExpectedDeposits(), ExpectedWithdrawals(), ExpectedConsolidations()];
        Block block = Build.A.Block.WithRequestsHash(ExpectedHash(expected)).TestObject;

        (byte[][]? requests, string? error) = FromBody(block, $$"""{ "deposits": [{{Deposit}}], "withdrawals": [{{Withdrawal}}], "consolidations": [{{Consolidation}}] }""");

        Assert.That(error, Is.Null);
        Assert.That(requests, Is.EqualTo(expected));
    }

    [Test]
    public void Skips_empty_groups()
    {
        byte[][] expected = [ExpectedConsolidations()];
        Block block = Build.A.Block.WithRequestsHash(ExpectedHash(expected)).TestObject;

        (byte[][]? requests, string? error) = FromBody(block, $$"""{ "deposits": [], "withdrawals": [], "consolidations": [{{Consolidation}}] }""");

        Assert.That(error, Is.Null);
        Assert.That(requests, Is.EqualTo(expected));
    }

    [Test]
    public void Sorts_groups_by_type()
    {
        byte[][] expected = [ExpectedDeposits(), ExpectedWithdrawals(), ExpectedConsolidations()];
        Block block = Build.A.Block.WithRequestsHash(ExpectedHash(expected)).TestObject;

        (byte[][]? requests, string? error) = FromBody(block, $$"""{ "consolidations": [{{Consolidation}}], "withdrawals": [{{Withdrawal}}], "deposits": [{{Deposit}}] }""");

        Assert.That(error, Is.Null);
        Assert.That(requests, Is.EqualTo(expected));
    }

    [Test]
    public void Rejects_requests_hash_mismatch()
    {
        Block block = Build.A.Block.WithRequestsHash(ExpectedHash([ExpectedDeposits()])).TestObject;

        (byte[][]? requests, string? error) = FromBody(block, $$"""{ "deposits": [{{Deposit}}], "withdrawals": [{{Withdrawal}}], "consolidations": [] }""");

        Assert.That(requests, Is.Null);
        Assert.That(error, Does.Contain("requests hash to"));
    }

    [Test]
    public void Rejects_block_hash_mismatch()
    {
        byte[][] expected = [ExpectedConsolidations()];
        Block block = Build.A.Block.WithRequestsHash(ExpectedHash(expected)).TestObject;

        (byte[][]? requests, string? error) = FromBody(block, $$"""{ "consolidations": [{{Consolidation}}] }""", TestItem.KeccakH);

        Assert.That(requests, Is.Null);
        Assert.That(error, Does.Contain("holds execution block"));
    }

    [Test]
    public void Rejects_unknown_request_group()
    {
        byte[][] expected = [ExpectedConsolidations()];
        Block block = Build.A.Block.WithRequestsHash(ExpectedHash(expected)).TestObject;

        (byte[][]? requests, string? error) = FromBody(block, $$"""{ "consolidations": [{{Consolidation}}], "builder_exits": [] }""");

        Assert.That(requests, Is.Null);
        Assert.That(error, Does.Contain("builder_exits"));
    }

    [Test]
    public async Task Fetches_requests_from_the_slot_of_the_block()
    {
        byte[][] expected = [ExpectedConsolidations()];
        Block block = Build.A.Block.WithTimestamp(GenesisTime + 5 * 12).WithRequestsHash(ExpectedHash(expected)).TestObject;
        using HttpClient client = Stub(path => path switch
        {
            "/eth/v2/beacon/blocks/5" => BeaconBlock(block, $$"""{ "consolidations": [{{Consolidation}}] }"""),
            _ => DefaultResponse(path)
        });

        (byte[][]? requests, string? error) = await BeaconRequests.TryFetch(BeaconUrl, block, CancellationToken.None, client);

        Assert.That(error, Is.Null);
        Assert.That(requests, Is.EqualTo(expected));
    }

    [TestCase("""{ "data": { "SECONDS_PER_SLOT": "0" } }""", "SECONDS_PER_SLOT = 0", TestName = "Zero seconds per slot")]
    [TestCase("""{ "data": { "SECONDS_PER_SLOT": null } }""", null, TestName = "Null seconds per slot")]
    [TestCase("""{ "data": { "SECONDS_PER_SLOT": "99999999999999999999999" } }""", null, TestName = "Seconds per slot overflow")]
    [TestCase("""{ "data": """, null, TestName = "Truncated JSON")]
    public async Task Falls_back_on_a_malformed_spec(string spec, string? expectedError)
    {
        Block block = Build.A.Block.WithTimestamp(GenesisTime + 12).WithRequestsHash(ExpectedHash([ExpectedConsolidations()])).TestObject;
        using HttpClient client = Stub(path => path == "/eth/v1/config/spec" ? spec : DefaultResponse(path));

        (byte[][]? requests, string? error) = await BeaconRequests.TryFetch(BeaconUrl, block, CancellationToken.None, client);

        Assert.That(requests, Is.Null);
        Assert.That(error, expectedError is null ? Is.Not.Null.And.Not.Empty : Does.Contain(expectedError));
    }

    [Test]
    public async Task Falls_back_when_the_beacon_node_times_out()
    {
        Block block = Build.A.Block.WithTimestamp(GenesisTime + 12).WithRequestsHash(ExpectedHash([ExpectedConsolidations()])).TestObject;
        using HttpClient client = new(new HangingHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };

        (byte[][]? requests, string? error) = await BeaconRequests.TryFetch(BeaconUrl, block, CancellationToken.None, client);

        Assert.That(requests, Is.Null);
        Assert.That(error, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Propagates_caller_cancellation()
    {
        Block block = Build.A.Block.WithTimestamp(GenesisTime + 12).WithRequestsHash(ExpectedHash([ExpectedConsolidations()])).TestObject;
        using HttpClient client = new(new HangingHandler());
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));

        Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), () => BeaconRequests.TryFetch(BeaconUrl, block, cts.Token, client));
    }

    private const ulong GenesisTime = 1_606_824_023;
    private static readonly Uri BeaconUrl = new("http://beacon.test/");

    private static string DefaultResponse(string path) => path switch
    {
        "/eth/v1/beacon/genesis" => $$"""{ "data": { "genesis_time": "{{GenesisTime}}" } }""",
        "/eth/v1/config/spec" => """{ "data": { "SECONDS_PER_SLOT": "12" } }""",
        _ => throw new HttpRequestException($"unexpected path {path}", null, HttpStatusCode.NotFound)
    };

    private static string BeaconBlock(Block block, string executionRequests) =>
        $$"""{ "data": { "message": { "body": { "execution_payload": { "block_hash": "{{block.Hash}}" }, "execution_requests": {{executionRequests}} } } } }""";

    private static HttpClient Stub(Func<string, string> respond) => new(new StubHandler(respond));

    private sealed class StubHandler(Func<string, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request.RequestUri!.AbsolutePath), Encoding.UTF8, "application/json")
            });
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }

    private static (byte[][]? Requests, string? Error) FromBody(Block block, string executionRequests, Hash256? blockHash = null)
    {
        using JsonDocument body = JsonDocument.Parse(
            $$"""{ "execution_payload": { "block_hash": "{{blockHash ?? block.Hash}}" }, "execution_requests": {{executionRequests}} }""");
        return BeaconRequests.FromBeaconBlockBody(body.RootElement, block);
    }

    private static string Deposit =>
        $$"""{ "pubkey": "{{Hex(DepositPubkey)}}", "withdrawal_credentials": "{{Hex(DepositCredentials)}}", "amount": "{{DepositAmount}}", "signature": "{{Hex(DepositSignature)}}", "index": "{{DepositIndex}}" }""";

    private static string Withdrawal =>
        $$"""{ "source_address": "{{Hex(WithdrawalSource)}}", "validator_pubkey": "{{Hex(WithdrawalPubkey)}}", "amount": "{{WithdrawalAmount}}" }""";

    private static string Consolidation =>
        $$"""{ "source_address": "{{Hex(ConsolidationSource)}}", "source_pubkey": "{{Hex(ConsolidationSourcePubkey)}}", "target_pubkey": "{{Hex(ConsolidationTargetPubkey)}}" }""";

    // EIP-6110: pubkey 48 + withdrawal_credentials 32 + amount u64 LE + signature 96 + index u64 LE.
    private static byte[] ExpectedDeposits() =>
        [0x00, .. DepositPubkey, .. DepositCredentials, .. LittleEndian(DepositAmount), .. DepositSignature, .. LittleEndian(DepositIndex)];

    // EIP-7002: source_address 20 + validator_pubkey 48 + amount u64 LE.
    private static byte[] ExpectedWithdrawals() =>
        [0x01, .. WithdrawalSource, .. WithdrawalPubkey, .. LittleEndian(WithdrawalAmount)];

    // EIP-7251: source_address 20 + source_pubkey 48 + target_pubkey 48.
    private static byte[] ExpectedConsolidations() =>
        [0x02, .. ConsolidationSource, .. ConsolidationSourcePubkey, .. ConsolidationTargetPubkey];

    // EIP-7685: sha256 over the sha256 of each non-empty type-prefixed group.
    private static Hash256 ExpectedHash(byte[][] groups) =>
        new(SHA256.HashData(groups.SelectMany(SHA256.HashData).ToArray()));

    private static byte[] LittleEndian(ulong value)
    {
        byte[] bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Fill(int size, byte seed) =>
        Enumerable.Range(0, size).Select(i => (byte)(seed + i)).ToArray();

    private static string Hex(byte[] bytes) => "0x" + Convert.ToHexStringLower(bytes);
}
