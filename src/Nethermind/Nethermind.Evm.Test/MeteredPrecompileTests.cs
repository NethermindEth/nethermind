// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class MeteredPrecompileTests
{
    private const string BusyName = "TEST_METERED_BUSY";
    private const string IdleName = "TEST_METERED_IDLE";

    [Test]
    public void PublishMetrics_ReportsRunsPerWrappedPrecompile()
    {
        CountingPrecompile busy = new(BusyName);
        CountingPrecompile idle = new(IdleName);
        MeteredPrecompileProvider provider = new(new TestPrecompileProvider(
            new Dictionary<AddressAsKey, CodeInfo>
            {
                [TestItem.AddressA] = new(busy),
                [TestItem.AddressB] = new(idle),
            }.ToFrozenDictionary()));

        FrozenDictionary<AddressAsKey, CodeInfo> precompiles = provider.GetPrecompiles();
        IPrecompile metered = precompiles[TestItem.AddressA].Precompile!;
        metered.Run(default, Prague.Instance);
        metered.Run(default, Prague.Instance);

        provider.PublishMetrics();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(precompiles.Keys, Is.EquivalentTo(new AddressAsKey[] { TestItem.AddressA, TestItem.AddressB }),
                "metering must not change which addresses are precompiles");
            Assert.That(metered.Name, Is.EqualTo(BusyName), "the label must come from the wrapped precompile, not the decorator");
            Assert.That(busy.Runs, Is.EqualTo(2), "the decorator must forward every call");
            Assert.That(Precompiles.Metrics.PrecompileRuns[BusyName], Is.EqualTo(2));
            Assert.That(Precompiles.Metrics.PrecompileRuns[IdleName], Is.Zero, "an unused precompile must still report its series");
        }
    }

    private sealed class CountingPrecompile(string name) : IPrecompile
    {
        public int Runs { get; private set; }

        public string Name => name;

        public ulong BaseGasCost(IReleaseSpec releaseSpec) => 0UL;

        public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => 0UL;

        public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
        {
            Runs++;
            return Array.Empty<byte>();
        }
    }

    private sealed class TestPrecompileProvider(FrozenDictionary<AddressAsKey, CodeInfo> precompiles) : IPrecompileProvider
    {
        public FrozenDictionary<AddressAsKey, CodeInfo> GetPrecompiles() => precompiles;
    }
}
