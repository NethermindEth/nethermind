// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// <see href="https://eips.ethereum.org/EIPS/eip-2537" />
/// </summary>
public partial class Bls12381G1MsmPrecompile : IPrecompile<Bls12381G1MsmPrecompile>
{
    public const int ItemSize = 160;

    // See: https://specs.optimism.io/protocol/isthmus/exec-engine.html#bls-precompiles
    private const int OpIsthmusMsmMaxInputSize = 513_760;

    // See: https://specs.optimism.io/protocol/jovian/exec-engine.html#precompile-input-size-restrictions
    private const int OpJovianMsmMaxInputSize = 288_960;

    public static Bls12381G1MsmPrecompile Instance { get; } = new();

    private Bls12381G1MsmPrecompile() { }

    public static Address Address { get; } = Address.FromNumber(0x0c);

    public static string Name => "BLS12_G1MSM";

    public long BaseGasCost(IReleaseSpec _) => 0L;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec _)
    {
        int k = inputData.Length / ItemSize;
        return 12000L * k * Eip2537.DiscountForG1(k) / 1000;
    }

    public partial Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec);

    private static bool ValidateInputLength(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        // Karst keeps the Jovian limit; the explicit check guards against chainspecs activating Karst without Jovian.
        int? maxInputSize =
            releaseSpec.IsOpKarstEnabled || releaseSpec.IsOpJovianEnabled ? OpJovianMsmMaxInputSize :
            releaseSpec.IsOpIsthmusEnabled ? OpIsthmusMsmMaxInputSize :
            null;
        return inputData.Length != 0 && inputData.Length % ItemSize == 0 && (maxInputSize is null || inputData.Length <= maxInputSize);
    }
}
