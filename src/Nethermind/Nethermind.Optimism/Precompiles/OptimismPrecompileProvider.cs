// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Optimism.Precompiles;

/// <summary>
/// Wraps the standard Ethereum precompiles, applying the OP-Stack input-size restrictions according
/// to the active Optimism forks, while delegating everything else unchanged.
/// </summary>
/// <remarks>
/// Assumes that the Karst fork implies Jovian.
/// <para>Input-size restrictions per fork:</para>
/// <list type="bullet">
/// <item><see href="https://specs.optimism.io/protocol/granite/exec-engine.html#bn256pairing-precompile-input-restriction">Granite</see></item>
/// <item><see href="https://specs.optimism.io/protocol/isthmus/exec-engine.html#bls-precompiles">Isthmus</see></item>
/// <item><see href="https://specs.optimism.io/protocol/jovian/exec-engine.html#precompile-input-size-restrictions">Jovian</see></item>
/// <item><see href="https://specs.optimism.io/protocol/karst/exec-engine.html#precompile-input-size-restrictions">Karst</see></item>
/// </list>
/// </remarks>
public class OptimismPrecompileProvider : IPrecompileProvider
{
    private static readonly FrozenDictionary<AddressAsKey, CodeInfo> _precompiles = CreatePrecompiles();

    private static FrozenDictionary<AddressAsKey, CodeInfo> CreatePrecompiles()
    {
        Dictionary<AddressAsKey, CodeInfo> dict = new(new EthereumPrecompileProvider().GetPrecompiles())
        {
            [BN254PairingCheckPrecompile.Address] = new(new InputSizeLimitedPrecompile(BN254PairingCheckPrecompile.Instance, Bn254PairingMaxInputSize)),
            [Bls12381G1MsmPrecompile.Address] = new(new InputSizeLimitedPrecompile(Bls12381G1MsmPrecompile.Instance, BlsG1MsmMaxInputSize)),
            [Bls12381G2MsmPrecompile.Address] = new(new InputSizeLimitedPrecompile(Bls12381G2MsmPrecompile.Instance, BlsG2MsmMaxInputSize)),
            [Bls12381PairingCheckPrecompile.Address] = new(new InputSizeLimitedPrecompile(Bls12381PairingCheckPrecompile.Instance, BlsPairingMaxInputSize)),
        };
        return dict.ToFrozenDictionary();
    }

    public FrozenDictionary<AddressAsKey, CodeInfo> GetPrecompiles() => _precompiles;

    private static int? Bn254PairingMaxInputSize(IReleaseSpec spec) => spec switch
    {
        IOptimismReleaseSpec { IsOpKarstEnabled: true } => 57_600,
        IOptimismReleaseSpec { IsOpJovianEnabled: true } => 81_984,
        IOptimismReleaseSpec { IsOpGraniteEnabled: true } => 112_687,
        _ => null,
    };

    private static int? BlsG1MsmMaxInputSize(IReleaseSpec spec) => spec switch
    {
        IOptimismReleaseSpec { IsOpJovianEnabled: true } => 288_960,
        IOptimismReleaseSpec { IsOpIsthmusEnabled: true } => 513_760,
        _ => null,
    };

    private static int? BlsG2MsmMaxInputSize(IReleaseSpec spec) => spec switch
    {
        IOptimismReleaseSpec { IsOpJovianEnabled: true } => 278_784,
        IOptimismReleaseSpec { IsOpIsthmusEnabled: true } => 488_448,
        _ => null,
    };

    private static int? BlsPairingMaxInputSize(IReleaseSpec spec) => spec switch
    {
        IOptimismReleaseSpec { IsOpJovianEnabled: true } => 156_672,
        IOptimismReleaseSpec { IsOpIsthmusEnabled: true } => 235_008,
        _ => null,
    };
}
