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
    public void Empty_bundle_has_valid_proofs_only_without_stray_commitments_or_proofs(
        [Values] ProofVersion version,
        [Values] bool hasCommitment,
        [Values] bool hasProof)
    {
        byte[][] commitments = hasCommitment ? [new byte[Ckzg.BytesPerCommitment]] : [];
        byte[][] proofs = hasProof ? [new byte[Ckzg.BytesPerProof]] : [];

        ShardBlobNetworkWrapper wrapper = new([], commitments, proofs, version);

        Assert.That(IBlobProofsManager.For(version).ValidateProofs(wrapper), Is.EqualTo(!hasCommitment && !hasProof));
    }
    [TestCase(ProofVersion.V0, true, false, false)]
    [TestCase(ProofVersion.V0, false, true, false)]
    [TestCase(ProofVersion.V0, true, true, false)]
    [TestCase(ProofVersion.V1, false, false, true)]
    [TestCase(ProofVersion.V1, true, false, false)]
    [TestCase(ProofVersion.V1, false, true, false)]
    [TestCase(ProofVersion.V1, true, true, false)]
    public void Empty_bundle_has_valid_proofs_only_without_stray_commitments_or_proofs(
        ProofVersion version,
        bool hasCommitment,
        bool hasProof,
        bool expected)
    {
        byte[][] commitments = hasCommitment ? [new byte[Ckzg.BytesPerCommitment]] : [];
        byte[][] proofs = hasProof ? [new byte[Ckzg.BytesPerProof]] : [];

        ShardBlobNetworkWrapper wrapper = new([], commitments, proofs, version);

        Assert.That(IBlobProofsManager.For(version).ValidateProofs(wrapper), Is.EqualTo(expected));
    }
}
