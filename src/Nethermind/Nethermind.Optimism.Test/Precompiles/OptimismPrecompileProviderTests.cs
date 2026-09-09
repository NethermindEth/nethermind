// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Optimism.Precompiles;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.Precompiles;

public class OptimismPrecompileProviderTests
{
    private static readonly FrozenDictionary<AddressAsKey, CodeInfo> Precompiles = new OptimismPrecompileProvider().GetPrecompiles();

    private static IPrecompile Resolve<T>() where T : IPrecompile<T> => Precompiles[T.Address].Precompile!;

    [TestCaseSource(nameof(OpInputSizeLimitCases))]
    public void SizeLimit(IPrecompile precompile, IReleaseSpec spec, int inputLength, bool expectSuccess)
    {
        Result<byte[]> result = precompile.Run(new byte[inputLength], spec);

        Assert.That(result.IsSuccess, Is.EqualTo(expectSuccess));
        if (!expectSuccess)
            Assert.That(result.Error, Is.EqualTo(Errors.InvalidInputLength));
    }

    private static IEnumerable<TestCaseData> OpInputSizeLimitCases()
    {
        // BN254 pairing — Granite 112,687 / Jovian 81,984 / Karst 57,600 bytes
        {
            const int pairSize = 192; // BN254.PairSize
            IPrecompile precompile = Resolve<BN254PairingCheckPrecompile>();
            IReleaseSpec preFork = new OptimismReleaseSpec();
            IReleaseSpec granite = new OptimismReleaseSpec { IsOpGraniteEnabled = true };
            IReleaseSpec jovian = new OptimismReleaseSpec { IsOpGraniteEnabled = true, IsOpJovianEnabled = true };
            IReleaseSpec karst = new OptimismReleaseSpec { IsOpGraniteEnabled = true, IsOpJovianEnabled = true, IsOpKarstEnabled = true };

            yield return new(precompile, preFork, 587 * pairSize, true) { TestName = "BN254 PreFork over Granite limit" };
            yield return new(precompile, preFork, 57_599, false) { TestName = "BN254 PreFork not a multiple of pair size" };
            yield return new(precompile, granite, 586 * pairSize, true) { TestName = "BN254 Granite at limit" };
            yield return new(precompile, granite, 587 * pairSize, false) { TestName = "BN254 Granite over limit" };
            yield return new(precompile, jovian, 427 * pairSize, true) { TestName = "BN254 Jovian at limit" };
            yield return new(precompile, jovian, 428 * pairSize, false) { TestName = "BN254 Jovian over limit" };
            yield return new(precompile, karst, 300 * pairSize, true) { TestName = "BN254 Karst at limit" };
            yield return new(precompile, karst, 301 * pairSize, false) { TestName = "BN254 Karst over limit" };
            yield return new(precompile, karst, 57_599, false) { TestName = "BN254 Karst not a multiple of pair size" };
        }

        // BLS12-381 G1 MSM — Isthmus 513,760 / Jovian(+Karst) 288,960 bytes
        {
            const int itemSize = Bls12381G1MsmPrecompile.ItemSize;
            IPrecompile precompile = Resolve<Bls12381G1MsmPrecompile>();
            IReleaseSpec preFork = new OptimismReleaseSpec();
            IReleaseSpec isthmus = new OptimismReleaseSpec { IsOpIsthmusEnabled = true };
            IReleaseSpec jovian = new OptimismReleaseSpec { IsOpIsthmusEnabled = true, IsOpJovianEnabled = true };
            IReleaseSpec karst = new OptimismReleaseSpec { IsOpIsthmusEnabled = true, IsOpJovianEnabled = true, IsOpKarstEnabled = true };

            yield return new(precompile, preFork, 3_212 * itemSize, true) { TestName = "BLS G1MSM PreFork over Isthmus limit" };
            yield return new(precompile, preFork, itemSize - 1, false) { TestName = "BLS G1MSM PreFork not a multiple of item size" };
            yield return new(precompile, isthmus, 3_211 * itemSize, true) { TestName = "BLS G1MSM Isthmus at limit" };
            yield return new(precompile, isthmus, 3_212 * itemSize, false) { TestName = "BLS G1MSM Isthmus over limit" };
            yield return new(precompile, jovian, 1_806 * itemSize, true) { TestName = "BLS G1MSM Jovian at limit" };
            yield return new(precompile, jovian, 1_807 * itemSize, false) { TestName = "BLS G1MSM Jovian over limit" };
            yield return new(precompile, karst, 1_806 * itemSize, true) { TestName = "BLS G1MSM Karst at Jovian limit" };
            yield return new(precompile, karst, 1_807 * itemSize, false) { TestName = "BLS G1MSM Karst over Jovian limit" };
        }

        // BLS12-381 G2 MSM — Isthmus 488,448 / Jovian(+Karst) 278,784 bytes
        {
            const int itemSize = Bls12381G2MsmPrecompile.ItemSize;
            IPrecompile precompile = Resolve<Bls12381G2MsmPrecompile>();
            IReleaseSpec preFork = new OptimismReleaseSpec();
            IReleaseSpec isthmus = new OptimismReleaseSpec { IsOpIsthmusEnabled = true };
            IReleaseSpec jovian = new OptimismReleaseSpec { IsOpIsthmusEnabled = true, IsOpJovianEnabled = true };
            IReleaseSpec karst = new OptimismReleaseSpec { IsOpIsthmusEnabled = true, IsOpJovianEnabled = true, IsOpKarstEnabled = true };

            yield return new(precompile, preFork, 1_697 * itemSize, true) { TestName = "BLS G2MSM PreFork over Isthmus limit" };
            yield return new(precompile, preFork, itemSize - 1, false) { TestName = "BLS G2MSM PreFork not a multiple of item size" };
            yield return new(precompile, isthmus, 1_696 * itemSize, true) { TestName = "BLS G2MSM Isthmus at limit" };
            yield return new(precompile, isthmus, 1_697 * itemSize, false) { TestName = "BLS G2MSM Isthmus over limit" };
            yield return new(precompile, jovian, 968 * itemSize, true) { TestName = "BLS G2MSM Jovian at limit" };
            yield return new(precompile, jovian, 969 * itemSize, false) { TestName = "BLS G2MSM Jovian over limit" };
            yield return new(precompile, karst, 968 * itemSize, true) { TestName = "BLS G2MSM Karst at Jovian limit" };
            yield return new(precompile, karst, 969 * itemSize, false) { TestName = "BLS G2MSM Karst over Jovian limit" };
        }

        // BLS12-381 pairing check — Isthmus 235,008 / Jovian(+Karst) 156,672 bytes
        {
            const int pairSize = 384; // Bls12381PairingCheckPrecompile.PairSize
            IPrecompile precompile = Resolve<Bls12381PairingCheckPrecompile>();
            IReleaseSpec preFork = new OptimismReleaseSpec();
            IReleaseSpec isthmus = new OptimismReleaseSpec { IsOpIsthmusEnabled = true };
            IReleaseSpec jovian = new OptimismReleaseSpec { IsOpIsthmusEnabled = true, IsOpJovianEnabled = true };
            IReleaseSpec karst = new OptimismReleaseSpec { IsOpIsthmusEnabled = true, IsOpJovianEnabled = true, IsOpKarstEnabled = true };

            yield return new(precompile, preFork, 613 * pairSize, true) { TestName = "BLS pairing PreFork over Isthmus limit" };
            yield return new(precompile, preFork, pairSize - 1, false) { TestName = "BLS pairing PreFork not a multiple of pair size" };
            yield return new(precompile, isthmus, 612 * pairSize, true) { TestName = "BLS pairing Isthmus at limit" };
            yield return new(precompile, isthmus, 613 * pairSize, false) { TestName = "BLS pairing Isthmus over limit" };
            yield return new(precompile, jovian, 408 * pairSize, true) { TestName = "BLS pairing Jovian at limit" };
            yield return new(precompile, jovian, 409 * pairSize, false) { TestName = "BLS pairing Jovian over limit" };
            yield return new(precompile, karst, 408 * pairSize, true) { TestName = "BLS pairing Karst at Jovian limit" };
            yield return new(precompile, karst, 409 * pairSize, false) { TestName = "BLS pairing Karst over Jovian limit" };
        }
    }
}
