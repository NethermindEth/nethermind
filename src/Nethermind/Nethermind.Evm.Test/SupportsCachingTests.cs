// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Evm.Precompiles;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class SupportsCachingTests
{
    public static IEnumerable<TestCaseData> PrecompilesWithCachingEnabled()
    {
        yield return new TestCaseData(ECRecoverPrecompile.Instance).SetName(nameof(ECRecoverPrecompile));
        yield return new TestCaseData(Sha256Precompile.Instance).SetName(nameof(Sha256Precompile));
        yield return new TestCaseData(Ripemd160Precompile.Instance).SetName(nameof(Ripemd160Precompile));
        yield return new TestCaseData(BN254AddPrecompile.Instance).SetName(nameof(BN254AddPrecompile));
        yield return new TestCaseData(BN254MulPrecompile.Instance).SetName(nameof(BN254MulPrecompile));
        yield return new TestCaseData(BN254PairingPrecompile.Instance).SetName(nameof(BN254PairingPrecompile));
        yield return new TestCaseData(ModExpPrecompile.Instance).SetName(nameof(ModExpPrecompile));
        yield return new TestCaseData(Blake2FPrecompile.Instance).SetName(nameof(Blake2FPrecompile));
        yield return new TestCaseData(Bls12381G1AddPrecompile.Instance).SetName(nameof(Bls12381G1AddPrecompile));
        yield return new TestCaseData(Bls12381G1MsmPrecompile.Instance).SetName(nameof(Bls12381G1MsmPrecompile));
        yield return new TestCaseData(Bls12381G2AddPrecompile.Instance).SetName(nameof(Bls12381G2AddPrecompile));
        yield return new TestCaseData(Bls12381G2MsmPrecompile.Instance).SetName(nameof(Bls12381G2MsmPrecompile));
        yield return new TestCaseData(Bls12381PairingCheckPrecompile.Instance).SetName(nameof(Bls12381PairingCheckPrecompile));
        yield return new TestCaseData(Bls12381FpToG1Precompile.Instance).SetName(nameof(Bls12381FpToG1Precompile));
        yield return new TestCaseData(Bls12381Fp2ToG2Precompile.Instance).SetName(nameof(Bls12381Fp2ToG2Precompile));
        yield return new TestCaseData(KzgPointEvaluationPrecompile.Instance).SetName(nameof(KzgPointEvaluationPrecompile));
        yield return new TestCaseData(SecP256r1Precompile.Instance).SetName(nameof(SecP256r1Precompile));
    }

    [TestCaseSource(nameof(PrecompilesWithCachingEnabled))]
    public void Precompile_SupportsCaching_ReturnsTrue_ByDefault(IPrecompile precompile)
    {
        Assert.That(precompile.SupportsCaching, Is.True);
    }

    [Test]
    public void IdentityPrecompile_SupportsCaching_ReturnsFalse()
    {
        Assert.That(IdentityPrecompile.Instance.SupportsCaching, Is.False);
    }
}
