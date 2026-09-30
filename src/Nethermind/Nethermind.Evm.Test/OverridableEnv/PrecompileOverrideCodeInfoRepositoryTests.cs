// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.OverridableEnv;

[Parallelizable(ParallelScope.All)]
public class PrecompileOverrideCodeInfoRepositoryTests
{
    /// <summary>
    /// A simulated block that crosses into a fork adding a precompile must dispatch it, even at an address whose code
    /// an earlier block overrode.
    /// </summary>
    [Test]
    public void ResetPrecompileOverrides_drops_code_override_at_address_the_next_spec_makes_a_precompile()
    {
        Address bls12G1Add = Address.FromNumber(0x0b);
        Assert.That(Cancun.Instance.IsPrecompile(bls12G1Add), Is.False);
        Assert.That(Prague.Instance.IsPrecompile(bls12G1Add), Is.True);

        IWorldState worldState = Substitute.For<IWorldState>();
        OverridableCodeInfoRepository repository = new(new EthereumCodeInfoRepository(worldState), worldState);
        repository.SetCodeOverride(Cancun.Instance, bls12G1Add, new CodeInfo(new byte[] { 0x00 }));

        repository.ResetPrecompileOverrides(Prague.Instance);

        Assert.That(repository.GetCachedCodeInfo(bls12G1Add, Prague.Instance).IsPrecompile, Is.True);
    }

    /// <summary>A call to a move destination records the account in the EIP-7928 block access list.</summary>
    [Test]
    public void GetCachedCodeInfo_at_move_destination_records_the_account_access()
    {
        Address movedTo = Address.FromNumber(0x123456);
        IWorldState worldState = Substitute.For<IWorldState>();
        CodeOverrideStore overrides = new();
        overrides.Precompiles[movedTo] = new CodeInfo(IdentityPrecompile.Instance);
        MovedPrecompileCodeInfoRepository repository = new(Substitute.For<ICodeInfoRepository>(), worldState, overrides);

        CodeInfo codeInfo = repository.GetCachedCodeInfo(movedTo, followDelegation: false, Prague.Instance, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(codeInfo.IsPrecompile, Is.True);
            worldState.Received().AddAccountRead(movedTo);
            worldState.Received().RecordAccountAccess(movedTo);
        }
    }
}
