// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test;

public class ForkTests
{
    [Test]
    public void GetLatest_Returns_BPO2()
    {
        Assert.That(Fork.GetLatest(), Is.EqualTo(BPO2.Instance));
    }

    [Test]
    public void GetLatest_Matches_FoundationJson()
    {
        // Load foundation.json — source of truth for mainnet forks
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/foundation.json");
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance);
        ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(path);

        // Build spec provider — discovers all transitions automatically
        var provider = new ChainSpecBasedSpecProvider(chainSpec);

        // Last timestamp transition = latest fork in foundation.json
        ForkActivation latestActivation = provider.TransitionActivations[^1];

        // Resolve to named fork via MainnetSpecProvider (use ParisBlockNumber to reach timestamp-based forks)
        IReleaseSpec latestSpec = MainnetSpecProvider.Instance.GetSpec(
            (MainnetSpecProvider.ParisBlockNumber, latestActivation.Timestamp));

        Assert.That(Fork.GetLatest().Name, Is.EqualTo(latestSpec.Name),
            "Fork.GetLatest() is out of sync with foundation.json. Update Fork.cs.");
    }
}
