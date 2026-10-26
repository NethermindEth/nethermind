// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    private static readonly Address ContextRegistry = new("0x1111111111111111111111111111111111111111");
    private static readonly Address ContextManager = new("0x2222222222222222222222222222222222222222");

    [Test]
    public void NativeContext_IndependentAbi_MatchesExactFlatEncoding([Values] bool actualParent)
    {
        NativeOfflineContext context = Context(actualParent);
        JsonElement fixture = StatelessFixtures.ReadJson("rollupx/native-profile-v1", "offline-context.json");
        JsonElement vector = fixture.GetProperty("vectors")[actualParent ? 0 : 1];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.CustomData.ToArray(), Is.EqualTo(Bytes.FromHexString(vector.GetProperty("expectedBytes").GetString()!)));
            Assert.That(ValueKeccak.Compute(context.CustomData), Is.EqualTo(new ValueHash256(vector.GetProperty("expectedKeccak256").GetString()!)));
            Assert.That(context.CustomData.Length, Is.EqualTo((actualParent ? 13 : 12) * 32));
            Assert.That(context.Selector, Is.EqualTo(actualParent ? ulong.MaxValue : 100UL));
            Assert.That(context.RollupId, Is.EqualTo(7UL));
            Assert.That(context.AcceptedL2BlockHash, Is.EqualTo(OfflineHash(0x66)));
        }
    }

    private static readonly string[] InvalidContextFields = ["registry", "manager", "rollup", "accepted", "hash", "number", "max", "time"];

    [Test]
    public void NativeContext_InvalidDeploymentOrAnchor_Rejects([ValueSource(nameof(InvalidContextFields))] string field)
    {
        NativeL1Header endpoint = ContextEndpoint();
        NativeEvidenceAnchor anchor = new(100, endpoint.Hash, 132);
        anchor = field switch
        {
            "hash" => anchor with { Hash = OfflineHash(0x99) },
            "number" => anchor with { Number = 99 },
            "max" => anchor with { Number = ulong.MaxValue },
            "time" => anchor with { TargetTimestamp = 120 },
            _ => anchor,
        };
        NativeOfflineProfile.Inputs profile = OfflineInput(0x11);
        ValueHash256 accepted = field == "accepted" ? default : OfflineHash(0x66);
        Assert.That(() => new NativeOfflineContext(in profile, [0, 1000], field == "registry" ? Address.Zero : ContextRegistry,
            field == "manager" ? Address.Zero : ContextManager, field == "rollup" ? 0UL : 7UL, in accepted, in anchor, endpoint),
            Throws.TypeOf<EezSettlementException>());
    }

    private static readonly string[] InvalidContextProfileFields =
        ["l1", "l2", "genesis", "config", "history", "collector", "system", "manager", "interval", "sync", "offset"];

    [Test]
    public void NativeContext_InvalidProfileShape_RejectsBeforeEncoding([ValueSource(nameof(InvalidContextProfileFields))] string field)
    {
        NativeOfflineProfile.Inputs profile = OfflineInput(0x11);
        profile = field switch
        {
            "l1" => profile with { L1ChainId = UInt256.Zero },
            "l2" => profile with { L2ChainId = UInt256.Zero },
            "genesis" => profile with { GenesisHash = default },
            "config" => profile with { L1ConfigHash = default },
            "history" => profile with { HistoryCodeHash = default },
            "collector" => profile with { FeeCollector = Address.Zero },
            "system" => profile with { FeeCollector = Address.SystemUser },
            "manager" => profile with { FeeCollector = EezConstants.RollupXEezl2Address },
            "interval" => profile with { BlockInterval = 0 },
            "sync" => profile with { SyncInterval = 1 },
            "offset" => profile with { UpdateOffset = 6 },
            _ => profile,
        };
        NativeL1Header endpoint = ContextEndpoint();
        NativeEvidenceAnchor anchor = new(100, endpoint.Hash, 132);
        ValueHash256 accepted = OfflineHash(0x66);
        Assert.That(() => new NativeOfflineContext(in profile, [0, 1000], ContextRegistry, ContextManager, 7, in accepted, in anchor, endpoint),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void NativeContext_HistoricalReservedSelector_Rejects([Values(0UL, ulong.MaxValue)] ulong number)
    {
        NativeL1Header endpoint = NativeL1Header.Decode(L1HeaderBytes(number, 120, default), LegacyHeaderRules);
        NativeEvidenceAnchor anchor = new(number, endpoint.Hash, null);
        NativeOfflineProfile.Inputs profile = OfflineInput(0x11);
        ValueHash256 accepted = OfflineHash(0x66);
        Assert.That(() => new NativeOfflineContext(in profile, [0, 1000], ContextRegistry, ContextManager, 7, in accepted, in anchor, endpoint),
            Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void NativeContext_ActualParentGenesisAnchor_Accepts()
    {
        NativeL1Header endpoint = NativeL1Header.Decode(L1HeaderBytes(0, 120, default), LegacyHeaderRules);
        NativeEvidenceAnchor anchor = new(0, endpoint.Hash, 132);
        NativeOfflineProfile.Inputs profile = OfflineInput(0x11);
        ValueHash256 accepted = OfflineHash(0x66);
        NativeOfflineContext context = new(in profile, [0, 1000], ContextRegistry, ContextManager, 7, in accepted, in anchor, endpoint);
        Assert.That(context.Selector, Is.EqualTo(ulong.MaxValue));
    }

    [TestCase(100UL, false, TestName = "HistoricalInclusion_SameHeight_Rejects")]
    [TestCase(99UL, false, TestName = "HistoricalInclusion_EarlierHeight_Rejects")]
    [TestCase(101UL, true, TestName = "HistoricalInclusion_AgeOne_Accepts")]
    [TestCase(356UL, true, TestName = "HistoricalInclusion_Age256_Accepts")]
    [TestCase(357UL, false, TestName = "HistoricalInclusion_Age257_Rejects")]
    public void NativeContext_HistoricalInclusion_EnforcesExactAge(ulong number, bool valid)
    {
        NativeOfflineContext context = Context(false);
        NativeL1Header inclusion = NativeL1Header.Decode(L1HeaderBytes(number, 132, default), LegacyHeaderRules);
        if (valid) Assert.DoesNotThrow(() => context.ValidateInclusion(inclusion));
        else Assert.That(() => context.ValidateInclusion(inclusion), Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void NativeContext_ActualParentInclusion_RequiresExactChild([Values("valid", "number", "parent", "time")] string field)
    {
        NativeOfflineContext context = Context(true);
        NativeL1Header inclusion = NativeL1Header.Decode(L1HeaderBytes(field == "number" ? 102UL : 101UL,
            field == "time" ? 133UL : 132UL, field == "parent" ? OfflineHash(0x99) : context.Anchor.Hash), LegacyHeaderRules);
        if (field == "valid") Assert.DoesNotThrow(() => context.ValidateInclusion(inclusion));
        else Assert.That(() => context.ValidateInclusion(inclusion), Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void NativeContext_HistoricalInclusion_CannotCrossEpoch([Values(999UL, 1000UL)] ulong timestamp)
    {
        NativeOfflineContext context = Context(false);
        NativeL1Header inclusion = NativeL1Header.Decode(L1HeaderBytes(101, timestamp, default), LegacyHeaderRules);
        if (timestamp < 1000) Assert.DoesNotThrow(() => context.ValidateInclusion(inclusion));
        else Assert.That(() => context.ValidateInclusion(inclusion), Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void NativeContext_RuleEpochs_AreOwnedAfterConstruction()
    {
        ulong[] epochs = [0, 1000];
        NativeOfflineContext context = Context(false, epochs);
        epochs[1] = 2000;
        NativeL1Header inclusion = NativeL1Header.Decode(L1HeaderBytes(101, 1000, default), LegacyHeaderRules);
        Assert.That(() => context.ValidateInclusion(inclusion), Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void NativeContext_ActualParentEpoch_UsesTargetInsteadOfEndpointTimestamp()
    {
        NativeOfflineProfile.Inputs profile = OfflineInput(0x11);
        NativeL1Header endpoint = ContextEndpoint();
        NativeEvidenceAnchor anchor = new(100, endpoint.Hash, 1000);
        ValueHash256 accepted = OfflineHash(0x66);
        NativeOfflineContext context = new(in profile, [0, 1000], ContextRegistry, ContextManager, 7, in accepted, in anchor, endpoint);
        NativeL1Header inclusion = NativeL1Header.Decode(L1HeaderBytes(101, 1000, endpoint.Hash), LegacyHeaderRules);
        context.ValidateInclusion(inclusion);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.RulesId, Is.EqualTo(NativeOfflineProfile.RulesId(profile.L1ConfigHash, [0, 1000], 1000)));
            Assert.That(context.RulesId, Is.Not.EqualTo(NativeOfflineProfile.RulesId(profile.L1ConfigHash, [0, 1000], 120)));
        }
    }

    private static readonly string[] ContextBindingFields = ["registry", "manager", "rollup", "accepted", "l1", "l2", "genesis", "collector", "config"];

    [Test]
    public void NativeContext_EachDeploymentBinding_ChangesCommittedBytes([ValueSource(nameof(ContextBindingFields))] string field)
    {
        NativeOfflineProfile.Inputs profile = OfflineInput(0x11);
        profile = field switch
        {
            "l1" => profile with { L1ChainId = 2 },
            "l2" => profile with { L2ChainId = 902 },
            "genesis" => profile with { GenesisHash = OfflineHash(0x34) },
            "collector" => profile with { FeeCollector = ContextManager },
            "config" => profile with { L1ConfigHash = OfflineHash(0x56) },
            _ => profile,
        };
        NativeL1Header endpoint = ContextEndpoint();
        NativeEvidenceAnchor anchor = new(100, endpoint.Hash, 132);
        ValueHash256 accepted = OfflineHash(field == "accepted" ? (byte)0x67 : (byte)0x66);
        NativeOfflineContext changed = new(in profile, [0, 1000], field == "registry" ? ContextManager : ContextRegistry,
            field == "manager" ? ContextRegistry : ContextManager, field == "rollup" ? 8UL : 7UL, in accepted, in anchor, endpoint);
        NativeOfflineContext original = Context(true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed.CustomData.ToArray(), Is.Not.EqualTo(original.CustomData.ToArray()));
            if (field == "accepted")
                Assert.That(new ValueHash256(changed.CustomData.Slice(8 * 32, 32)), Is.EqualTo(accepted), "accepted block hash has its own exact ABI word");
        }
    }

    [Test]
    public void NativeContext_ActualParent_CanCreateExactLiveSource()
    {
        NativeOfflineContext context = Context(true);
        NativeLiveSource expected = new(1, ContextRegistry, 7, 901, 132, context.Anchor.Hash);
        NativeLiveSource actual = context.LiveSource();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.InboundDomain, Is.EqualTo(expected.InboundDomain));
            Assert.That(actual.Outbound(2, OfflineHash(0x88)), Is.EqualTo(expected.Outbound(2, OfflineHash(0x88))));
            Assert.That(() => Context(false).LiveSource(), Throws.TypeOf<EezSettlementException>());
        }
    }

    [Test]
    public void NativeContext_WideChains_EncodeWithoutTruncationButRejectLiveSource([Values] bool l1)
    {
        NativeOfflineProfile.Inputs profile = l1
            ? OfflineInput(0x11) with { L1ChainId = UInt256.MaxValue }
            : OfflineInput(0x11) with { L2ChainId = UInt256.MaxValue };
        NativeL1Header endpoint = ContextEndpoint();
        NativeEvidenceAnchor anchor = new(100, endpoint.Hash, 132);
        ValueHash256 accepted = OfflineHash(0x66);
        NativeOfflineContext context = new(in profile, [0], ContextRegistry, ContextManager, 7, in accepted, in anchor, endpoint);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.CustomData.Slice((l1 ? 1 : 5) * 32, 32).IndexOfAnyExcept((byte)0xff), Is.EqualTo(-1));
            Assert.That(() => context.LiveSource(), Throws.TypeOf<EezSettlementException>());
        }
    }

    [Test]
    public void NativeContext_ExactUint64ChainLimit_CanCreateLiveSource([Values] bool l1)
    {
        NativeOfflineProfile.Inputs profile = l1
            ? OfflineInput(0x11) with { L1ChainId = (UInt256)ulong.MaxValue }
            : OfflineInput(0x11) with { L2ChainId = (UInt256)ulong.MaxValue };
        NativeL1Header endpoint = ContextEndpoint();
        NativeEvidenceAnchor anchor = new(100, endpoint.Hash, 132);
        ValueHash256 accepted = OfflineHash(0x66);
        NativeOfflineContext context = new(in profile, [0], ContextRegistry, ContextManager, 7, in accepted, in anchor, endpoint);
        NativeLiveSource actual = context.LiveSource();
        NativeLiveSource expected = new(l1 ? ulong.MaxValue : 1UL, ContextRegistry, 7, l1 ? 901UL : ulong.MaxValue, 132, endpoint.Hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.InboundDomain, Is.EqualTo(expected.InboundDomain));
            Assert.That(actual.OutboundDomain, Is.EqualTo(expected.OutboundDomain));
        }
    }

    private static NativeOfflineContext Context(bool actualParent, ulong[]? epochs = null)
    {
        NativeOfflineProfile.Inputs profile = OfflineInput(0x11);
        NativeL1Header endpoint = ContextEndpoint();
        NativeEvidenceAnchor anchor = new(100, endpoint.Hash, actualParent ? 132UL : null);
        ValueHash256 accepted = OfflineHash(0x66);
        return new NativeOfflineContext(in profile, epochs ?? [0, 1000], ContextRegistry, ContextManager, 7, in accepted, in anchor, endpoint);
    }

    private static NativeL1Header ContextEndpoint()
    {
        JsonElement fixture = StatelessFixtures.ReadJson("rollupx/native-profile-v1", "offline-context.json");
        byte[] bytes = Bytes.FromHexString(fixture.GetProperty("endpointRlp").GetString()!);
        return NativeL1Header.Decode(bytes, LegacyHeaderRules);
    }
}
