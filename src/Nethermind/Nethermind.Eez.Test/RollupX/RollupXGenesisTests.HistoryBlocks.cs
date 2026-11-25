// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    [Test]
    public void HistoryBlocks_EmptyStore_UsesConfiguredOriginWithoutPublishingIt()
    {
        NativeL1HeaderChain chain = HistoryChain([108, 119]);
        RollupXSchedule schedule = ProxyHistorySchedule(chain);
        NativeHistoryState state = NativeProxyHistory.ResolveState(0, default, schedule);
        NativeHistoryExecution execution = new NativeHistorySchedule(120, 2, 6, 2).VerifyBlocks(126, in state, chain, null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Imported, Is.False);
            Assert.That(execution.Imported, Is.False);
            Assert.That(execution.OriginNumber, Is.EqualTo(99UL));
            Assert.That(execution.OriginHash, Is.EqualTo(chain.At(99).Hash));
            Assert.That(execution.PrevRandao, Is.EqualTo(chain.At(99).PrevRandao));
            Assert.That(execution.Import.IsEmpty, Is.True);
        }
    }

    [Test]
    public void HistoryBlocks_FirstImport_CanPublishBootstrapOrSkipAhead([Values(99UL, 100UL)] ulong selected)
    {
        NativeL1HeaderChain chain = HistoryChain([108, 119]);
        NativeHistoryState state = NativeProxyHistory.ResolveState(0, default, ProxyHistorySchedule(chain));
        NativeHistoryEnvelope envelope = NativeHistoryEnvelope.CreateBlocks(1, [new(selected, chain.At(selected).Hash)]);
        NativeHistoryExecution execution = new NativeHistorySchedule(120, 2, 6, 2).VerifyBlocks(126, in state, chain, envelope);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Imported, Is.True);
            Assert.That(execution.OriginNumber, Is.EqualTo(selected));
            Assert.That(execution.OriginHash, Is.EqualTo(chain.At(selected).Hash));
            Assert.That(execution.PrevRandao, Is.EqualTo(chain.At(selected).PrevRandao));
            Assert.That(execution.Import.IsEmpty, Is.True, "semantic verification does not allocate execution calldata");
        }
        NativeHistoryState imported = state with { Imported = true, Endpoint = selected, EndpointHash = execution.OriginHash };
        Assert.That(() => new NativeHistorySchedule(120, 2, 6, 2).VerifyBlocks(138, in imported, chain, envelope),
            Throws.TypeOf<EezSettlementException>(), "the same record cannot be imported twice");
    }

    [Test]
    public void HistoryBlocks_SparseRecords_AuthenticateEverySelectedHeaderWithoutLimitingDistance()
    {
        ulong[] times = new ulong[80];
        for (int index = 0; index < times.Length; index++) times[index] = (ulong)index + 1;
        NativeL1HeaderChain chain = HistoryChain(times);
        NativeHistoryState state = new(0, 99, chain.At(99).Hash, 120);
        NativeHistoryEnvelope envelope = NativeHistoryEnvelope.CreateBlocks(1, [new(103, chain.At(103).Hash), new(178, chain.At(178).Hash)]);
        NativeHistoryExecution execution = new NativeHistorySchedule(120, 2, 6, 2).VerifyBlocks(126, in state, chain, envelope);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.OriginNumber, Is.EqualTo(178UL));
            Assert.That(execution.PrevRandao, Is.EqualTo(chain.At(178).PrevRandao));
            Assert.That(NativeHistoryCall.EncodeBlocks(envelope).Length, Is.EqualTo(68 + 64 * 2));
        }
    }

    [Test]
    public void HistoryBlocks_InvalidContext_RejectsBeforeExecution(
        [Values("genesis", "alignment", "off-schedule", "future", "parent", "first-record", "last-record", "missing-header", "regression")] string field)
    {
        NativeL1HeaderChain chain = HistoryChain([108, 119, 121]);
        NativeHistoryState state = new(0, 99, chain.At(99).Hash, 120, false);
        ulong timestamp = field switch { "genesis" => 120, "alignment" => 125, "off-schedule" => 128, _ => 126 };
        NativeHistoryBlock[] blocks = [new(99, chain.At(99).Hash), new(100, chain.At(100).Hash)];
        switch (field)
        {
            case "future": blocks = [new(101, chain.At(101).Hash)]; break;
            case "parent": state = state with { EndpointHash = TestItem.KeccakD.ValueHash256 }; break;
            case "first-record": blocks[0] = blocks[0] with { Hash = TestItem.KeccakD.ValueHash256 }; break;
            case "last-record": blocks[1] = blocks[1] with { Hash = TestItem.KeccakD.ValueHash256 }; break;
            case "missing-header": blocks = [new(102, TestItem.KeccakD.ValueHash256)]; break;
            case "regression": state = state with { Imported = true, Endpoint = 100, EndpointHash = chain.At(100).Hash }; break;
        }
        NativeHistoryEnvelope envelope = NativeHistoryEnvelope.CreateBlocks(1, blocks);
        Assert.That(() => new NativeHistorySchedule(120, 2, 6, 2).VerifyBlocks(timestamp, in state, chain, envelope),
            Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void HistoryBlocks_InvalidStorage_Rejects([Values("zero-hash", "zero-number", "before-bootstrap")] string field)
    {
        NativeL1HeaderChain chain = HistoryChain([108]);
        ulong number = field switch { "zero-number" => 0, "before-bootstrap" => 98, _ => 99 };
        ValueHash256 hash = field == "zero-hash" ? default : chain.At(99).Hash;
        Assert.That(() => NativeProxyHistory.ResolveState(number, hash, ProxyHistorySchedule(chain)), Throws.TypeOf<EezSettlementException>());
    }

    private static RollupXSchedule ProxyHistorySchedule(NativeL1HeaderChain chain) => new(2, 6,
        new RollupXL1Reference(1, 99, new Hash256(chain.At(99).Hash), 108, new Hash256(chain.At(99).PrevRandao)), 120);
}
