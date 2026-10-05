// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Net;
using Autofac;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using NSubstitute;

namespace Nethermind.BeaconChain.Test.Engine;

public class EngineDriverForkchoiceVersionTests
{
    private static readonly BeaconChainSpec Spec = TestEngineDriver.Spec;
    private static readonly ulong FirstGloasSlot = Spec.GloasForkEpoch * Spec.SlotsPerEpoch;
    private static Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Valid() => Task.FromResult(ForkchoiceUpdatedV1Result.Valid(null, TestItem.KeccakA));
    private static NodeColumnCustody DefaultCustody() => new(TestItem.KeccakD, Eip7594DasConstants.CustodyRequirement);

    private static (EngineDriver Driver, IEngineRpcModule Inner) Create(ulong slot, NodeColumnCustody? custody = null)
    {
        IEngineRpcModule inner = Substitute.For<IEngineRpcModule>();
        inner.engine_forkchoiceUpdatedV3(default!, default).ReturnsForAnyArgs(Valid());
        inner.engine_forkchoiceUpdatedV4(default!, default, default).ReturnsForAnyArgs(Valid());
        ExternalClDetector detector = new(new BeaconChainConfig { Enabled = true }, new Lazy<IEngineRpcModule>(inner), LimboLogs.Instance);
        _ = new ExternalClInterceptingEngineRpcModule(inner, detector);
        return (TestEngineDriver.Create(detector, slot, custody), inner);
    }

    // forkchoiceUpdatedV3 remains valid after Amsterdam without payload attributes (execution-apis amsterdam.md; paris.md point 8).
    [Test]
    public async Task Forkchoice_update_before_Gloas_is_v3_without_payload_attributes_or_custody()
    {
        Hash256 head = TestItem.KeccakA;
        Hash256 safe = TestItem.KeccakB;
        Hash256 finalized = TestItem.KeccakC;
        (EngineDriver driver, IEngineRpcModule inner) = Create(FirstGloasSlot - 1, DefaultCustody());

        await driver.ForkchoiceUpdated(head, safe, finalized);

        await inner.Received(1).engine_forkchoiceUpdatedV3(
            Arg.Is<ForkchoiceStateV1>(s => s.HeadBlockHash == head && s.SafeBlockHash == safe && s.FinalizedBlockHash == finalized),
            Arg.Is<PayloadAttributes?>(a => a == null));
        await inner.DidNotReceiveWithAnyArgs().engine_forkchoiceUpdatedV4(default!, default, default);
        await inner.DidNotReceiveWithAnyArgs().engine_forkchoiceUpdatedV5(default!, default, default);
    }

    // Gloas custody_columns requires forkchoiceUpdatedV4 (EIP-8070; execution-apis amsterdam.md).
    [Test]
    public async Task Forkchoice_update_from_Gloas_is_v4_with_the_exact_custody_columns(
        [Values(0UL, 1UL, 100_000UL)] ulong slotsIntoGloas,
        [Values(Eip7594DasConstants.CustodyRequirement, Eip7594DasConstants.SamplesPerSlot, Eip7594DasConstants.NumberOfCustodyGroups)] ulong custodyGroupCount)
    {
        Hash256 head = TestItem.KeccakA;
        NodeColumnCustody custody = new(TestItem.KeccakD, custodyGroupCount);
        (EngineDriver driver, IEngineRpcModule inner) = Create(FirstGloasSlot + slotsIntoGloas, custody);

        PayloadStatusV1 status = await driver.ForkchoiceUpdated(head, TestItem.KeccakB, TestItem.KeccakC);

        await inner.Received(1).engine_forkchoiceUpdatedV4(
            Arg.Is<ForkchoiceStateV1>(s => s.HeadBlockHash == head && s.SafeBlockHash == TestItem.KeccakB && s.FinalizedBlockHash == TestItem.KeccakC),
            Arg.Is<PayloadAttributes?>(a => a == null),
            Arg.Is<BitArray?>(b => HasExactlyColumns(b, custody.CustodyColumns)));
        await inner.DidNotReceiveWithAnyArgs().engine_forkchoiceUpdatedV3(default!, default);
        Assert.That(driver.LastForkchoiceStatus, Is.SameAs(status), "a V4 answer is the head status the caller and the status metrics read");
    }

    // Below 8 custody groups, sampling is larger than custody, exposing sample-derived custody bits.
    [Test]
    public void Custody_columns_of_the_default_group_count_are_a_strict_subset_of_the_sampled_columns()
    {
        NodeColumnCustody custody = new(TestItem.KeccakD, Eip7594DasConstants.CustodyRequirement);

        Assert.That(custody.CustodyColumns, Has.Count.LessThan(custody.SampledColumns.Count));
    }

    // Null means no CL custody services (execution-apis amsterdam.md).
    [Test]
    public async Task Forkchoice_update_from_Gloas_sends_no_custody_while_the_node_identity_is_unknown()
    {
        (EngineDriver driver, IEngineRpcModule inner) = Create(FirstGloasSlot);

        await driver.ForkchoiceUpdated(TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC);

        await inner.Received(1).engine_forkchoiceUpdatedV4(Arg.Any<ForkchoiceStateV1>(), Arg.Is<PayloadAttributes?>(a => a == null), Arg.Is<BitArray?>(b => b == null));
    }

    [Test]
    public void Forkchoice_update_rejected_by_the_execution_layer_surfaces_as_unavailable()
    {
        (EngineDriver driver, IEngineRpcModule inner) = Create(FirstGloasSlot, DefaultCustody());
        inner.engine_forkchoiceUpdatedV4(default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(ResultWrapper<ForkchoiceUpdatedV1Result>.Fail("Custody columns must be exactly 16 bytes.", ErrorCodes.InvalidParams)));

        Assert.ThrowsAsync<EngineUnavailableException>(() => driver.ForkchoiceUpdated(TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC));

        Assert.That(driver.LastForkchoiceStatus, Is.Null);
    }

    [Test]
    public async Task Container_built_driver_sends_the_custody_of_the_identity_discovery_advertises()
    {
        IEngineRpcModule inner = Substitute.For<IEngineRpcModule>();
        inner.engine_forkchoiceUpdatedV4(default!, default, default).ReturnsForAnyArgs(Valid());
        ManualTimestamper timestamper = new(DateTimeOffset.FromUnixTimeSeconds((long)(Spec.GenesisTime + FirstGloasSlot * Spec.SecondsPerSlot)).UtcDateTime);
        await using IContainer container = BeaconChainTestContainer.Builder(BlockchainIds.Sepolia, engine: inner, config: new BeaconChainConfig { Discv5Port = 0 })
            .AddSingleton<ITimestamper>(timestamper)
            .Build();
        BeaconDiscovery discovery = container.Resolve<BeaconDiscovery>();
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        NodeColumnCustody advertised = new(discovery.LocalCustody.NodeId, discovery.LocalCustody.CustodyGroupCount);

        await container.Resolve<EngineDriver>().ForkchoiceUpdated(TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC);

        await inner.Received(1).engine_forkchoiceUpdatedV4(
            Arg.Any<ForkchoiceStateV1>(),
            Arg.Is<PayloadAttributes?>(a => a == null),
            Arg.Is<BitArray?>(b => HasExactlyColumns(b, advertised.CustodyColumns)));
    }

    private static bool HasExactlyColumns(BitArray? bits, IEnumerable<ulong> columns) =>
        bits is not null && bits.Length == Eip7594DasConstants.NumberOfColumns
            && Enumerable.Range(0, bits.Length).Where(i => bits[i]).Select(i => (ulong)i).SequenceEqual(columns.Order());
}
