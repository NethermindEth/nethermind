// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test;

public class ForkTests
{
    [Test]
    public void GetLatest_Matches_FoundationJson()
    {
        // Load foundation.json — source of truth for mainnet forks
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "../../../../", "Chains/foundation.json");
        ChainSpecFileLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
        ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(path);

        // Build spec provider — discovers all transitions automatically
        ChainSpecBasedSpecProvider provider = new(chainSpec);

        // Last timestamp transition = latest fork in foundation.json
        ForkActivation latestActivation = provider.TransitionActivations[^1];
        IReleaseSpec chainSpecLatest = provider.GetSpec(latestActivation);
        IReleaseSpec forkLatest = Fork.GetLatest();

        // Compare all properties except Name (ChainSpecBasedSpecProvider doesn't set it)
        using (Assert.EnterMultipleScope())
        {
            foreach (System.Reflection.PropertyInfo property in typeof(IReleaseSpec).GetProperties())
            {
                if (property.Name == nameof(IReleaseSpec.Name))
                {
                    continue;
                }

                Assert.That(property.GetValue(forkLatest), Is.EqualTo(property.GetValue(chainSpecLatest)), property.Name);
            }
        }
    }

    [Test]
    public void Bogota_scales_blob_schedule_by_slot_duration_ratio()
    {
        IReleaseSpec bogota = Bogota.Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bogota.MaxBlobCount, Is.EqualTo(14));
            Assert.That(bogota.TargetBlobCount, Is.EqualTo(9));
            Assert.That(bogota.BlobBaseFeeUpdateFraction, Is.EqualTo((UInt256)7789780));
        }
    }
}
