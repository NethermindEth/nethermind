// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Guards against re-introducing a second, hand-copied source of truth for the Electra blob limit
/// this driver feeds into ProcessExecutionPayload for operations vectors.
/// </summary>
[TestFixture]
public class FuluDriverSupportTests
{
    [Test]
    public void MaxBlobsPerBlockElectra_matches_the_mainnet_spec_it_is_derived_from() =>
        Assert.That(FuluDriverSupport.MaxBlobsPerBlockElectra, Is.EqualTo(BeaconChainSpec.Mainnet.MaxBlobsPerBlockElectra));
}
