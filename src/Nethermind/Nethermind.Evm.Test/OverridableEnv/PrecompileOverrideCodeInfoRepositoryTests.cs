// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.OverridableEnv;

[Parallelizable(ParallelScope.All)]
public class PrecompileOverrideCodeInfoRepositoryTests
{
    [Test]
    public void Account_overrides_preserve_native_dispatch_only_when_opted_in(
        [Values] bool preservePrecompiles,
        [Values("empty", "balance", "nonce", "code", "state", "stateDiff")] string field)
    {
        IReleaseSpec spec = Prague.Instance;
        Address address = IdentityPrecompile.Address;
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository inner = new(worldState);
        OverridableCodeInfoRepository repository = preservePrecompiles
            ? new(inner, worldState, precompileOverridePolicy: new(disableOnAccountOverride: false))
            : new(inner, worldState);
        Dictionary<UInt256, Hash256> storage = new() { [UInt256.One] = Hash256.FromBytesWithPadding([2]) };
        AccountOverride accountOverride = field switch
        {
            "balance" => new() { Balance = 7 },
            "nonce" => new() { Nonce = 3 },
            "code" => new() { Code = [(byte)Instruction.STOP] },
            "state" => new() { State = storage },
            "stateDiff" => new() { StateDiff = storage },
            _ => new()
        };

        worldState.ApplyStateOverridesNoCommit(repository, new() { [address] = accountOverride }, spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.GetCachedCodeInfo(address, spec).IsPrecompile, Is.EqualTo(preservePrecompiles));
            Assert.That(repository.GetPrecompile(address, spec), Is.EqualTo(preservePrecompiles ? IdentityPrecompile.Instance : null));
            if (field is "state" or "stateDiff")
            {
                worldState.Get(new StorageCell(address, UInt256.One), out UInt256 value);
                Assert.That(value, Is.EqualTo((UInt256)2));
            }
            if (field == "code") Assert.That(worldState.GetCode(address).Span, Is.SequenceEqualTo(new byte[] { (byte)Instruction.STOP }));
            if (field == "balance") Assert.That(worldState.GetBalance(address), Is.EqualTo((UInt256)7));
            if (field == "nonce") Assert.That(worldState.GetNonce(address), Is.EqualTo(3UL));
        }
    }

    [Test]
    public void Preserving_precompiles_does_not_block_ordinary_code_overrides([Values] bool preservePrecompiles)
    {
        IReleaseSpec spec = Prague.Instance;
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        OverridableCodeInfoRepository repository = new(new EthereumCodeInfoRepository(worldState), worldState,
            precompileOverridePolicy: new(disableOnAccountOverride: !preservePrecompiles));

        worldState.ApplyStateOverridesNoCommit(repository, new()
        {
            [TestItem.AddressA] = new() { Code = [(byte)Instruction.STOP] }
        }, spec);

        Assert.That(repository.GetCachedCodeInfo(TestItem.AddressA, spec).CodeSpan, Is.SequenceEqualTo(new byte[] { (byte)Instruction.STOP }));
    }

    [Test]
    public void Explicit_moves_still_allow_code_at_the_removed_native_origin([Values] bool preservePrecompiles, [Values] bool dynamicAddress)
    {
        IReleaseSpec spec = Prague.Instance;
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        Address source = dynamicAddress ? TestItem.AddressB : IdentityPrecompile.Address;
        ICodeInfoRepository inner = dynamicAddress ? Substitute.For<ICodeInfoRepository>() : new EthereumCodeInfoRepository(worldState);
        if (dynamicAddress)
        {
            inner.GetPrecompile(source, spec).Returns(IdentityPrecompile.Instance);
            inner.GetCachedCodeInfo(source, false, spec, out _).ReturnsForAnyArgs(new CodeInfo(IdentityPrecompile.Instance));
        }
        OverridableCodeInfoRepository repository = new(inner, worldState,
            precompileOverridePolicy: new(disableOnAccountOverride: !preservePrecompiles));

        worldState.ApplyStateOverridesNoCommit(repository, new()
        {
            [source] = new() { MovePrecompileToAddress = TestItem.AddressA, Code = [(byte)Instruction.STOP] }
        }, spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.GetPrecompile(source, spec), Is.Null);
            Assert.That(repository.GetCachedCodeInfo(source, spec).CodeSpan, Is.SequenceEqualTo(new byte[] { (byte)Instruction.STOP }));
            Assert.That(repository.GetPrecompile(TestItem.AddressA, spec), Is.SameAs(IdentityPrecompile.Instance));
        }
    }

    /// <summary>
    /// A simulated block that crosses into a fork adding a precompile must dispatch it, even at an address whose code
    /// an earlier block overrode.
    /// </summary>
    [Test]
    public void ResetPrecompileOverrides_drops_code_override_at_address_the_next_spec_makes_a_precompile()
    {
        Address bls12G1Add = Address.FromNumber(0x0b);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Cancun.Instance.IsPrecompile(bls12G1Add), Is.False);
            Assert.That(Prague.Instance.IsPrecompile(bls12G1Add), Is.True);
        }

        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
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

    /// <summary>Before Prague a code override holding an EIP-7702 designator is ordinary code, as in geth.</summary>
    [Test]
    public void GetCachedCodeInfo_follows_overridden_delegation_only_when_eip7702_is_enabled([Values] bool movedPrecompileRepository, [Values] bool eip7702Enabled)
    {
        IReleaseSpec spec = eip7702Enabled ? Prague.Instance : Cancun.Instance;
        Address overridden = Address.FromNumber(1);
        Address target = TestItem.AddressB;
        byte[] designator = [.. Eip7702Constants.DelegationHeader, .. target.Bytes];
        byte[] targetCode = [(byte)Instruction.STOP];

        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        worldState.CreateAccount(target, 0);
        worldState.InsertCode(target, targetCode, spec);
        EthereumCodeInfoRepository inner = new(worldState);
        CodeOverrideStore overrides = new();
        OverridableCodeInfoRepository overridable = new(inner, worldState, overrides);
        overridable.SetCodeOverride(spec, overridden, new CodeInfo(designator));
        ICodeInfoRepository repository = movedPrecompileRepository ? new MovedPrecompileCodeInfoRepository(inner, worldState, overrides) : overridable;

        CodeInfo codeInfo = repository.GetCachedCodeInfo(overridden, followDelegation: true, spec, out Address? delegation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(codeInfo.CodeSpan, Is.SequenceEqualTo(eip7702Enabled ? targetCode : designator));
            Assert.That(delegation, Is.EqualTo(eip7702Enabled ? target : null));
        }
    }
}
