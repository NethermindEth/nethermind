// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Guards against the harness counting any exception at all as a correct rejection of an invalid vector.
/// </summary>
[TestFixture]
public class FuluDriverSupportTests
{
    [Test]
    public void AssertRejected_passes_only_the_pipelines_own_rejection_types()
    {
        Assert.DoesNotThrow(() => FuluDriverSupport.AssertRejected(new BeaconStateException("failed spec assertion"), "the operation"));
        Assert.DoesNotThrow(() => FuluDriverSupport.AssertRejected(new ForkChoiceException("failed spec assertion"), "the block"));
    }

    /// <summary>A vector that completes, or crashes on the way to rejecting, must not be recorded as a correct rejection.</summary>
    [Test]
    public void AssertRejected_fails_a_vector_that_completed_or_threw_for_the_wrong_reason()
    {
        Assert.That(() => FuluDriverSupport.AssertRejected(null, "the operation"),
            Throws.TypeOf<AssertionException>().With.Message.Contains("completed without error"));
        Assert.That(() => FuluDriverSupport.AssertRejected(new NullReferenceException(), "the operation"),
            Throws.TypeOf<AssertionException>().With.Message.Contains(nameof(NullReferenceException)));
    }
}
