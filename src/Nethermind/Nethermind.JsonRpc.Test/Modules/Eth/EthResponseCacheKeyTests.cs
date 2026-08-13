// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

public class EthResponseCacheKeyTests
{
    private static readonly Hash256 BlockHash = TestItem.KeccakA;
    private static readonly EthereumJsonSerializer Serializer = new();
    private static readonly string A = TestItem.AddressA.ToString();
    private static readonly string B = TestItem.AddressB.ToString();

    public static IEnumerable<TestCaseData> KeyCases()
    {
        yield return new TestCaseData(
            $$"""{"to": "{{A}}", "value": "0x1", "gas": "0x5208", "data": "0x1234"}""",
            $$"""{"to": "{{A}}", "value": "0x1", "gas": "0x5208", "data": "0x1234"}""",
            true).SetName("Identical calls share a key");
        yield return new TestCaseData(
            $$"""{"to": "{{A}}"}""",
            $$"""{"from": "{{A}}"}""",
            false).SetName("To-only and from-only with the same address differ");
        yield return new TestCaseData(
            $$"""{"to": "{{A}}", "data": "0x1234"}""",
            $$"""{"to": "{{A}}", "data": "0x123456"}""",
            false).SetName("Different calldata differs");
        yield return new TestCaseData(
            $$"""{"to": "{{A}}", "nonce": "0x0"}""",
            $$"""{"to": "{{A}}"}""",
            false).SetName("Explicit zero nonce and absent nonce differ");
        yield return new TestCaseData(
            $$"""{"to": "{{A}}", "gas": "0x0"}""",
            $$"""{"to": "{{A}}"}""",
            false).SetName("Explicit zero gas and absent gas differ");
        yield return new TestCaseData(
            $$"""{"to": "{{A}}", "type": "0x0"}""",
            $$"""{"to": "{{A}}", "type": "0x2"}""",
            false).SetName("Different transaction types differ");
        yield return new TestCaseData(
            $$"""{"to": "{{A}}", "accessList": [{"address": "{{B}}"}]}""",
            $$"""{"to": "{{A}}", "accessList": [{"address": "{{B}}", "storageKeys": []}]}""",
            true).SetName("Null and empty storage keys build the same access list and share a key");
        yield return new TestCaseData(
            $$"""{"to": "{{A}}", "accessList": [{"address": "{{A}}", "storageKeys": ["0x01"]}, {"address": "{{B}}"}]}""",
            $$"""{"to": "{{A}}", "accessList": [{"address": "{{A}}"}, {"address": "{{B}}", "storageKeys": ["0x01"]}]}""",
            false).SetName("Storage key attached to a different access list entry differs");
        yield return new TestCaseData(
            $$"""{"to": "{{A}}", "blobVersionedHashes": ["0x0100000000000000000000000000000000000000000000000000000000000000"], "maxFeePerBlobGas": "0x1"}""",
            $$"""{"to": "{{A}}", "blobVersionedHashes": ["0x0100000000000000000000000000000000000000000000000000000000000000"], "maxFeePerBlobGas": "0x2"}""",
            false).SetName("Different maxFeePerBlobGas differs");
        yield return new TestCaseData(
            $$"""{"to": "{{A}}", "authorizationList": [{"chainId": "0x1", "nonce": "0x1", "address": "{{B}}", "yParity": "0x0", "r": "0x1", "s": "0x1"}]}""",
            $$"""{"to": "{{A}}", "authorizationList": [{"chainId": "0x1", "nonce": "0x2", "address": "{{B}}", "yParity": "0x0", "r": "0x1", "s": "0x1"}]}""",
            false).SetName("Different authorization tuple differs");
    }

    [TestCaseSource(nameof(KeyCases))]
    public void Call_key_equality_tracks_execution_input_equality(string callA, string callB, bool expectEqual)
    {
        ValueHash256 keyA = EthResponseCache.ComputeCallKey(BlockHash, Serializer.Deserialize<TransactionForRpc>(callA));
        ValueHash256 keyB = EthResponseCache.ComputeCallKey(BlockHash, Serializer.Deserialize<TransactionForRpc>(callB));

        Assert.That(keyA == keyB, Is.EqualTo(expectEqual));
    }

    [Test]
    public void Unknown_runtime_type_falls_back_to_json_keying()
    {
        ExtendedLegacyTransactionForRpc call = new() { To = TestItem.AddressA };
        ExtendedLegacyTransactionForRpc different = new() { To = TestItem.AddressB };

        ValueHash256 key = EthResponseCache.ComputeCallKey(BlockHash, call);
        ValueHash256 repeatedKey = EthResponseCache.ComputeCallKey(BlockHash, call);
        ValueHash256 differentKey = EthResponseCache.ComputeCallKey(BlockHash, different);

        Assert.That(repeatedKey, Is.EqualTo(key));
        Assert.That(differentKey, Is.Not.EqualTo(key));
    }

    private sealed class ExtendedLegacyTransactionForRpc : LegacyTransactionForRpc;
}
