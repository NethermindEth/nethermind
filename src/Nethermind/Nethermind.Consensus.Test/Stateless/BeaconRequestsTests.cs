// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
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
