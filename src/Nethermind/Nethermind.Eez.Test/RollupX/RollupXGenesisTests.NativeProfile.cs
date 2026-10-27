// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    [Test]
    public void NativeProfile_IndependentVector_MatchesAbiAndCommitment([Values("profile-collector-a", "profile-collector-b")] string id, [Values(3UL, 4UL)] ulong version)
    {
        NativeOfflineProfile.Inputs input = OfflineInput(id == "profile-collector-a" ? (byte)0x11 : (byte)0x12) with { ValidationVersion = version };
        JsonElement vector = OfflineVector(id, version);
        NativeOfflineProfile.Validate(in input, [0, 1000], input.L1ChainId, input.HistoryCodeHash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(NativeOfflineProfile.Encode(in input, [0, 1000]), Is.EqualTo(Bytes.FromHexString(vector.GetProperty("expectedBytes").GetString()!)));
            Assert.That(NativeOfflineProfile.Compute(in input, [0, 1000]), Is.EqualTo(new ValueHash256(vector.GetProperty("expectedKeccak256").GetString()!)));
        }
    }

    [Test]
    public void NativeProfile_FullWidthChainIds_RetainsAll256Bits()
    {
        NativeOfflineProfile.Inputs input = OfflineInput(0x11) with { L1ChainId = UInt256.MaxValue, L2ChainId = UInt256.MaxValue };
        byte[] encoded = NativeOfflineProfile.Encode(in input, [0]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded.AsSpan(6 * 32, 32).IndexOfAnyExcept((byte)0xff), Is.EqualTo(-1));
            Assert.That(encoded.AsSpan(7 * 32, 32).IndexOfAnyExcept((byte)0xff), Is.EqualTo(-1));
        }
    }

    [Test]
    public void NativeProfile_RuleEpochs_SelectAtTheExactBoundary()
    {
        ValueHash256 config = OfflineHash(0x55);
        ValueHash256 before = NativeOfflineProfile.RulesId(config, [0, 1000], 999);
        ValueHash256 at = NativeOfflineProfile.RulesId(config, [0, 1000], 1000);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.EqualTo(NativeOfflineProfile.RulesId(config, [0], ulong.MaxValue)));
            Assert.That(at, Is.Not.EqualTo(before));
            Assert.That(at, Is.EqualTo(NativeOfflineProfile.RulesId(config, [0, 1000], ulong.MaxValue)));
            Assert.That(at, Is.Not.EqualTo(NativeOfflineProfile.RulesId(OfflineHash(0x56), [0, 1000], 1000)));
        }
    }

    [Test]
    public void NativeProfile_RulesId_MatchesIndependentAbiKeccak()
    {
        // Independent ABI/Keccak result from cast 1.7.1 for (bytes32,bytes32,uint64).
        ValueHash256 expected = new("0xa85ca94f196accd9aeed0e2dab273540e3f9a6ff78ab777c114a58f0a7788c85");
        Assert.That(NativeOfflineProfile.RulesId(OfflineHash(0x55), [0, 1000], 1000), Is.EqualTo(expected));
    }

    [Test]
    public void NativeProfile_RulesId_MissingConfiguration_Rejects() =>
        Assert.That(() => NativeOfflineProfile.RulesId(default, [0, 1000], 1000), Throws.TypeOf<ArgumentException>());

    [Test]
    public void NativeProfile_UnsupportedVersion_RejectsBeforeEncoding([Values(0UL, 1UL, 2UL, 5UL, ulong.MaxValue)] ulong version)
    {
        NativeOfflineProfile.Inputs profile = OfflineInput(0x11) with { ValidationVersion = version };
        RollupXValidationIdentity.Inputs identity = new(1, 901, Address.SystemUser, EezConstants.RollupXEezl2Address,
            new Hash256(OfflineHash(0x11)), new Hash256(OfflineHash(0x12)), new Address("0x1111111111111111111111111111111111111111"),
            new Address("0x2222222222222222222222222222222222222222"), version);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => NativeOfflineProfile.Encode(in profile, [0]), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => NativeOfflineProfile.Validate(in profile, [0], profile.L1ChainId, profile.HistoryCodeHash), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => RollupXValidationIdentity.Encode(in identity), Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }

    private static IEnumerable<TestCaseData> InvalidOfflineEpochs()
    {
        yield return new TestCaseData(Array.Empty<ulong>()).SetName("NativeProfile_EmptyRuleEpochs_Rejects");
        yield return new TestCaseData(new ulong[] { 1 }).SetName("NativeProfile_NoZeroRuleEpoch_Rejects");
        yield return new TestCaseData(new ulong[] { 0, 0 }).SetName("NativeProfile_DuplicateRuleEpoch_Rejects");
        yield return new TestCaseData(new ulong[] { 0, 1000, 999 }).SetName("NativeProfile_DecreasingRuleEpoch_Rejects");
        ulong[] excess = new ulong[65];
        for (int index = 0; index < excess.Length; index++) excess[index] = (ulong)index;
        yield return new TestCaseData(excess).SetName("NativeProfile_65RuleEpochs_Rejects");
    }

    [TestCaseSource(nameof(InvalidOfflineEpochs))]
    public void NativeProfile_InvalidRuleEpochs_RejectBeforeEncodingOrSelection(ulong[] times)
    {
        NativeOfflineProfile.Inputs input = OfflineInput(0x11);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => NativeOfflineProfile.Encode(in input, times), Throws.TypeOf<ArgumentException>());
            Assert.That(() => NativeOfflineProfile.RulesId(input.L1ConfigHash, times, 1000), Throws.TypeOf<ArgumentException>());
            Assert.That(() => NativeOfflineProfile.Validate(in input, times, input.L1ChainId, input.HistoryCodeHash), Throws.TypeOf<ArgumentException>());
        }
    }

    [Test]
    public void NativeProfile_MaximumRuleEpochs_Accepts()
    {
        ulong[] times = new ulong[64];
        for (int index = 0; index < times.Length; index++) times[index] = (ulong)index;
        NativeOfflineProfile.Inputs input = OfflineInput(0x11);
        Assert.That(NativeOfflineProfile.Encode(in input, times).Length, Is.EqualTo((6 + 11 + 1 + 64) * 32));
    }

    private static readonly string[] InvalidOfflineIdentityFields =
        ["l1", "l2", "genesis", "config", "history", "expected-history", "expected-l1", "zero-collector", "system-collector", "manager-collector"];

    [Test]
    public void NativeProfile_InvalidLocalIdentity_Rejects([ValueSource(nameof(InvalidOfflineIdentityFields))] string field)
    {
        NativeOfflineProfile.Inputs input = OfflineInput(0x11);
        input = field switch
        {
            "l1" => input with { L1ChainId = UInt256.Zero },
            "l2" => input with { L2ChainId = UInt256.Zero },
            "genesis" => input with { GenesisHash = default },
            "config" => input with { L1ConfigHash = default },
            "history" => input with { HistoryCodeHash = default },
            "zero-collector" => input with { FeeCollector = Address.Zero },
            "system-collector" => input with { FeeCollector = Address.SystemUser },
            "manager-collector" => input with { FeeCollector = EezConstants.RollupXEezl2Address },
            _ => input,
        };
        ValueHash256 expectedHistory = field == "expected-history" ? default : OfflineHash(0x44);
        UInt256 expectedL1 = field == "expected-l1" ? 2U : 1U;
        Assert.That(() => NativeOfflineProfile.Validate(in input, [0, 1000], in expectedL1, in expectedHistory), Throws.TypeOf<ArgumentException>());
    }

    private static IEnumerable<TestCaseData> HistoryCutoffCases() =>
    [
        new(120UL, 108UL), new(122UL, 108UL), new(124UL, 108UL), new(126UL, 120UL),
        new(130UL, 120UL), new(132UL, 120UL), new(136UL, 120UL), new(138UL, 132UL), new(150UL, 144UL),
    ];

    [TestCaseSource(nameof(HistoryCutoffCases))]
    public void NativeHistorySchedule_SourceCutoff_RetainsDelayAcrossSync(ulong timestamp, ulong expected)
    {
        NativeHistorySchedule schedule = new(120, 2, 6, 2);
        Assert.That(schedule.SourceCutoff(timestamp), Is.EqualTo(expected));
    }

    [Test]
    public void NativeHistorySchedule_OffsetExtremes_Accept([Values(0U, 5U)] uint offset)
    {
        NativeHistorySchedule schedule = new(120, 2, 6, offset);
        ulong delay = offset == 0 ? 2UL : 12UL;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.Interval, Is.EqualTo(12UL));
            Assert.That(schedule.Delay, Is.EqualTo(delay));
            Assert.That(schedule.SourceCutoff(120 + delay - 2), Is.EqualTo(108UL));
            Assert.That(schedule.SourceCutoff(120 + delay), Is.EqualTo(120UL));
        }
    }

    private static IEnumerable<TestCaseData> InvalidHistorySchedules() =>
    [
        new(0UL, 2UL, 6U, 2U), new(120UL, 0UL, 6U, 2U), new(120UL, 2UL, 1U, 0U),
        new(120UL, 2UL, 6U, 6U), new(ulong.MaxValue, 2UL, 6U, 2U),
        new(120UL, ulong.MaxValue, 6U, 2U), new(120UL, 1UL << 63, 2U, 0U),
    ];

    [TestCaseSource(nameof(InvalidHistorySchedules))]
    public void NativeHistorySchedule_InvalidBoundary_Rejects(ulong first, ulong interval, uint positions, uint offset) =>
        Assert.That(() => new NativeHistorySchedule(first, interval, positions, offset), Throws.TypeOf<ArgumentException>());

    [TestCaseSource(nameof(InvalidHistorySchedules))]
    public void NativeProfile_InvalidSchedule_Rejects(ulong first, ulong interval, uint positions, uint offset)
    {
        NativeOfflineProfile.Inputs input = OfflineInput(0x11) with
        {
            GenesisTimestamp = first,
            BlockInterval = interval,
            SyncInterval = positions,
            UpdateOffset = offset,
        };
        Assert.That(() => NativeOfflineProfile.Validate(in input, [0, 1000], input.L1ChainId, input.HistoryCodeHash), Throws.TypeOf<ArgumentException>());
    }

    [TestCase(12UL, 6U, 0UL)]
    [TestCase(120UL, 2U, 116UL)]
    [TestCase(ulong.MaxValue - 6, 6U, ulong.MaxValue - 18)]
    public void NativeHistorySchedule_ExactValidBoundaries_Accept(ulong first, uint positions, ulong bootstrap)
    {
        NativeHistorySchedule schedule = new(first, 2, positions, positions == 2 ? 0U : 2U);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.SourceCutoff(first), Is.EqualTo(bootstrap));
            Assert.That(schedule.SourceCutoff(first + schedule.Delay), Is.EqualTo(first));
        }
    }

    [Test]
    public void NativeHistorySchedule_InvalidTimestamp_Rejects([Values(119UL, 123UL, ulong.MaxValue)] ulong timestamp) =>
        Assert.That(() => new NativeHistorySchedule(120, 2, 6, 2).SourceCutoff(timestamp), Throws.TypeOf<ArgumentException>());

    private static NativeOfflineProfile.Inputs OfflineInput(byte collector)
    {
        byte[] address = new byte[20];
        address.AsSpan().Fill(collector);
        return new NativeOfflineProfile.Inputs(1, 901, OfflineHash(0x33), 120, 2, 6, 2, OfflineHash(0x55), OfflineHash(0x44), new Address(address));
    }

    private static ValueHash256 OfflineHash(byte value)
    {
        Span<byte> bytes = stackalloc byte[32];
        bytes.Fill(value);
        return new ValueHash256(bytes);
    }

    private static JsonElement OfflineVector(string id, ulong version = 3)
    {
        JsonElement fixture = StatelessFixtures.ReadJson($"rollupx/native-profile-v{version}", "vectors.json");
        foreach (JsonElement vector in fixture.GetProperty("vectors").EnumerateArray())
            if (vector.GetProperty("id").GetString() == id) return vector;
        throw new InvalidOperationException($"Missing native profile vector {id}.");
    }
}
