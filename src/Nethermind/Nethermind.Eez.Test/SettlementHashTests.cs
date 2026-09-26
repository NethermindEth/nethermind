// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class SettlementHashTests
{
    private const string Fixture = "settlement";

    private static readonly ValueHash256 ProxyEntryHash = new("0x0000000000000000000000000000000000000000000000000000000000000033");
    private static readonly ValueHash256 CallHash = new("0x0000000000000000000000000000000000000000000000000000000000000044");

    [Test]
    public void RollingHash_EntryEvents_MatchTheContractVectors()
    {
        JsonElement expected = StatelessFixtures.ReadJson(Fixture, "rolling_hash_vectors.json");
        StateCommitment[] states =
        [
            new(1, new ValueHash256("0x0000000000000000000000000000000000000000000000000000000000000011")),
            new(ulong.MaxValue, new ValueHash256("0x0000000000000000000000000000000000000000000000000000000000000022")),
        ];

        ValueHash256 l1Seed = RollingHash.SeedL1(states, ProxyEntryHash);
        ValueHash256 afterCallBegin = RollingHash.CallBegin(l1Seed, CallHash);
        ValueHash256 afterCallEnd = RollingHash.CallEnd(afterCallBegin, true, [0xaa, 0xbb, 0xcc]);
        ValueHash256 afterNestedBegin = RollingHash.NestedBegin(afterCallEnd, CallHash);
        ValueHash256 afterNestedEnd = RollingHash.NestedEnd(afterNestedBegin);
        ValueHash256 afterCallNotFound = RollingHash.CallNotFound(afterNestedEnd, CallHash);

        Assert.That(l1Seed, Is.EqualTo(Expected(expected, "l1Seed")), "the L1 seed folds (rollupId, currentState) in order, then the proxy entry hash");
        Assert.That(RollingHash.SeedL2(ProxyEntryHash), Is.EqualTo(Expected(expected, "l2Seed")), "the L2 seed binds only the proxy entry hash");
        Assert.That(afterCallBegin, Is.EqualTo(Expected(expected, "afterCallBegin")), "CALL_BEGIN folds the tag and the call hash");
        Assert.That(afterCallEnd, Is.EqualTo(Expected(expected, "afterCallEnd")), "CALL_END folds the tag, the success byte and the raw return data");
        Assert.That(afterNestedBegin, Is.EqualTo(Expected(expected, "afterNestedBegin")), "NESTED_BEGIN folds the tag and the call hash");
        Assert.That(afterNestedEnd, Is.EqualTo(Expected(expected, "afterNestedEnd")), "NESTED_END folds only the tag");
        Assert.That(afterCallNotFound, Is.EqualTo(Expected(expected, "afterCallNotFound")), "CALL_NOT_FOUND folds the tag and the call hash");
    }

    [Test]
    public void RollingHash_StaticResults_MatchTheContractVectors()
    {
        JsonElement expected = StatelessFixtures.ReadJson(Fixture, "rolling_hash_vectors.json");

        ValueHash256 afterSuccess = RollingHash.StaticResult(default, true, [0xaa, 0xbb, 0xcc]);
        ValueHash256 afterFailure = RollingHash.StaticResult(afterSuccess, false, [0xde, 0xad, 0xbe, 0xef]);

        Assert.That(afterSuccess, Is.EqualTo(Expected(expected, "afterStaticSuccess")), "static results fold untagged from a zero seed");
        Assert.That(afterFailure, Is.EqualTo(Expected(expected, "afterStaticFailure")), "a failed static result folds a zero success byte");
    }

    [TestCase(false, 7UL, 1UL, "0", 0UL, "0x010203", "0x16b1575ff5a4ec44167aebf047dd46f77db3766f7481445ad09c8136bff735a8", TestName = "CommonMutable")]
    [TestCase(true, 7UL, 1UL, "0", 0UL, "0x010203", "0x4cf0f2738ced4dcd497cf8a081030f41c5dc588fbdcac75f3a217e979d19abe7", TestName = "CommonStatic")]
    [TestCase(false, ulong.MaxValue, ulong.MaxValue - 1, "max", 0UL, "0x", "0x414b9d6bf91a3e266bcd34ddd870a53332107a606b6eda618455f9f940291e2b", TestName = "CommonBoundaries")]
    [TestCase(false, 1UL, 7UL, "1000000000000000000", 0UL, "0x010203", "0x9fd05cd7eebaf1d08b2961cb5d1237ef586cea58141270697a5509c6f3a03a37", TestName = "OutboundWithoutGas")]
    [TestCase(false, 1UL, 7UL, "1000000000000000000", 123_456UL, "0x010203", "0x25400cdd749a1c3ac82f4e3093f0460afe21e718a545a96f9399b9ae486c99e4", TestName = "OutboundWithGas")]
    [TestCase(true, 1UL, 7UL, "1000000000000000000", 0UL, "0x010203", "0xa5aeac7d89f6ef62251b7ab3a1645a75f30a11d9f15627ea3885ae49dd0940d3", TestName = "OutboundStatic")]
    [TestCase(false, ulong.MaxValue, ulong.MaxValue - 1, "max", ulong.MaxValue, "0x", "0x7f04915c437db6536fe9d746b135ed834b391532e4be8beadd898ad1f592895f", TestName = "OutboundBoundaries")]
    public void CrossChainCallHash_MatchesTheContractVectors(bool isStatic, ulong sourceRollupId, ulong targetRollupId, string value, ulong callGas, string data, string expected)
    {
        UInt256 amount = value == "max" ? UInt256.MaxValue : UInt256.Parse(value);

        ValueHash256 hash = CrossChainCallHash.Compute(isStatic, new Address("0x00000000000000000000000000000000000000bb"), sourceRollupId,
            new Address("0x00000000000000000000000000000000000000aa"), targetRollupId, amount, callGas, Bytes.FromHexString(data));

        Assert.That(hash, Is.EqualTo(new ValueHash256(expected)), "the call identity is keccak256 of the abi-encoded eight fields");
    }

    [TestCaseSource(nameof(PublicInputVectors))]
    public void PublicInputs_Vector_MatchesTheContract(JsonElement vector)
    {
        RollupProofAssignment[] assignments = vector.GetProperty("rollupAssignments").EnumerateArray().Select(static a => new RollupProofAssignment(
            a.GetProperty("rollupId").GetUInt64(),
            a.GetProperty("proofSystemIndexes").EnumerateArray().Select(static i => i.GetUInt64()).ToArray(),
            Hashes(a.GetProperty("vkeys")),
            Bytes.FromHexString(a.GetProperty("customData").GetString()!))).ToArray();
        ValueHash256[] customDataHashes = assignments.Select(static a => PublicInputs.CustomDataHash(a.RollupId, a.CustomData)).ToArray();
        byte[] callData = Bytes.FromHexString(vector.GetProperty("callData").GetString()!);
        Address boundSender = new(vector.GetProperty("boundSender").GetString()!);

        ValueHash256 shared = PublicInputs.SharedInput(Hashes(vector.GetProperty("entryHashes")), Hashes(vector.GetProperty("staticEntryHashes")),
            Hashes(vector.GetProperty("blobHashes")), callData, customDataHashes, boundSender);
        ValueHash256[] hashes = PublicInputs.Compute(Hashes(vector.GetProperty("entryHashes")), Hashes(vector.GetProperty("staticEntryHashes")),
            Hashes(vector.GetProperty("blobHashes")), callData, assignments, vector.GetProperty("proofSystemCount").GetInt32(), boundSender);

        Assert.That(shared, Is.EqualTo(new ValueHash256(vector.GetProperty("expectedSharedPublicInput").GetString()!)),
            "the shared input binds every hash array, the calldata hash and the bound sender");
        Assert.That(hashes, Is.EqualTo(Hashes(vector.GetProperty("expectedPublicInputsHashes"))),
            "each proof system binds the shared input to the rollups and verification keys assigned to it");
    }

    private static TestCaseData[] PublicInputVectors() => StatelessFixtures.ReadJson(Fixture, "public_inputs_hash_vectors.json")
        .GetProperty("vectors").EnumerateArray()
        .Select(static v => new TestCaseData(v) { TestName = v.GetProperty("name").GetString() })
        .ToArray();

    private static ValueHash256 Expected(JsonElement vectors, string name) => new(vectors.GetProperty(name).GetString()!);

    private static ValueHash256[] Hashes(JsonElement array) => array.EnumerateArray().Select(static h => new ValueHash256(h.GetString()!)).ToArray();
}
