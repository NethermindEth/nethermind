// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using Nethermind.Core;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto;

public class BlobProofsManagerTests
{
    [Test]
    public void Empty_bundle_has_valid_proofs([Values] ProofVersion version)
    {
        ShardBlobNetworkWrapper wrapper = new([], [], [], version);

        Assert.That(IBlobProofsManager.For(version).ValidateProofs(wrapper), Is.True);
    }

    [Test]
    public void Empty_bundle_with_stray_commitments_is_invalid([Values] ProofVersion version)
    {
        ShardBlobNetworkWrapper wrapper = new([], [new byte[Ckzg.BytesPerCommitment]], [], version);

        Assert.That(IBlobProofsManager.For(version).ValidateProofs(wrapper), Is.False);
    }
}
