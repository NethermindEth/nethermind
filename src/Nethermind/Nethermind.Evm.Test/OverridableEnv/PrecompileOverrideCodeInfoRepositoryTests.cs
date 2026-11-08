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
using Nethermind.State;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test.OverridableEnv;

[Parallelizable(ParallelScope.All)]
public class PrecompileOverrideCodeInfoRepositoryTests
{
    [Test]
    public void Retained_native_overrides_cache_dispatch_and_plain_code_views(
        [Values] bool movedRepository, [Values] bool dynamicAddress, [Values] bool designatorFirst)
    {
        IReleaseSpec spec = Prague.Instance;
        Address address = dynamicAddress ? TestItem.AddressB : IdentityPrecompile.Address;
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        ICodeInfoRepository inner = dynamicAddress ? Substitute.For<ICodeInfoRepository>() : new EthereumCodeInfoRepository(worldState);
        if (dynamicAddress) inner.GetPrecompile(address, spec).Returns(IdentityPrecompile.Instance);
        CodeOverrideStore overrides = new();
        OverridableCodeInfoRepository repository = new(inner, worldState, overrides, new(disableOnAccountOverride: false));
        ICodeInfoRepository reader = movedRepository ? new MovedPrecompileCodeInfoRepository(inner, worldState, overrides) : repository;
        byte[] ordinary = [(byte)Instruction.STOP];
        byte[] designator = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressA.Bytes];

        foreach (byte[] code in designatorFirst ? new[] { designator, ordinary } : new[] { ordinary, designator })
        {
            worldState.ApplyStateOverridesNoCommit(repository, new() { [address] = new() { Code = code } }, spec);
            CodeInfo dispatch = reader.GetCachedCodeInfo(address, followDelegation: true, spec, out Address? delegated);
            CodeInfo plain = reader.GetDelegatedCodeInfo(address, spec);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(dispatch.Precompile, Is.SameAs(IdentityPrecompile.Instance));
                Assert.That(dispatch.CodeSpan, Is.SequenceEqualTo(code));
                Assert.That(delegated, Is.Null, "a native precompile does not follow its visible designator");
                Assert.That(plain.IsPrecompile, Is.False);
                Assert.That(plain.CodeSpan, Is.SequenceEqualTo(code));
                Assert.That(reader.GetPrecompile(address, spec), Is.SameAs(IdentityPrecompile.Instance));
                Assert.That(reader.GetCachedCodeInfo(address, followDelegation: false, spec, out _), Is.SameAs(dispatch));
                Assert.That(reader.GetDelegatedCodeInfo(address, spec), Is.SameAs(plain), "no per-lookup code-info allocation");
            }
        }
    }

    [Test]
    public void Retained_native_code_views_follow_code_writes_reverts_and_env_reset()
    {
        IReleaseSpec spec = Prague.Instance;
        Address address = IdentityPrecompile.Address;
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        OverridableCodeInfoRepository repository = new(new EthereumCodeInfoRepository(worldState), worldState,
            precompileOverridePolicy: new(disableOnAccountOverride: false));
        Snapshot beforeOverride = worldState.TakeSnapshot();
        worldState.ApplyStateOverridesNoCommit(repository, new() { [address] = new() { Code = [(byte)Instruction.STOP] } }, spec);
        Snapshot beforeWrite = worldState.TakeSnapshot();
        worldState.InsertCode(address, new byte[] { (byte)Instruction.INVALID }, spec);
        CodeInfo changed = repository.GetCachedCodeInfo(address, spec);
        CodeInfo changedPlain = repository.GetDelegatedCodeInfo(address, spec);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed.Precompile, Is.SameAs(IdentityPrecompile.Instance));
            Assert.That(changed.CodeSpan, Is.SequenceEqualTo(new byte[] { (byte)Instruction.INVALID }));
            Assert.That(changedPlain.IsPrecompile, Is.False);
            Assert.That(changedPlain.CodeSpan.SequenceEqual(changed.CodeSpan), Is.True);
            Assert.That(repository.GetCachedCodeInfo(address, spec), Is.SameAs(changed));
            Assert.That(repository.GetDelegatedCodeInfo(address, spec), Is.SameAs(changedPlain));
        }

        worldState.Restore(beforeWrite);
        CodeInfo restored = repository.GetCachedCodeInfo(address, spec);
        CodeInfo restoredPlain = repository.GetDelegatedCodeInfo(address, spec);
        repository.ResetPrecompileOverrides(spec);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.Precompile, Is.SameAs(IdentityPrecompile.Instance));
            Assert.That(restored.CodeSpan, Is.SequenceEqualTo(new byte[] { (byte)Instruction.STOP }));
            Assert.That(restoredPlain.IsPrecompile, Is.False);
            Assert.That(restoredPlain.CodeSpan.SequenceEqual(restored.CodeSpan), Is.True);
            Assert.That(repository.GetCachedCodeInfo(address, spec), Is.SameAs(restored));
            Assert.That(repository.GetDelegatedCodeInfo(address, spec), Is.SameAs(restoredPlain));
        }

        worldState.Restore(beforeOverride);
        repository.ResetOverrides();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.GetCachedCodeInfo(address, spec).Precompile, Is.SameAs(IdentityPrecompile.Instance));
            Assert.That(repository.GetCachedCodeInfo(address, spec).CodeSpan.IsEmpty, Is.True);
            Assert.That(repository.GetDelegatedCodeInfo(address, spec).CodeSpan.IsEmpty, Is.True);
        }
    }

    [Test]
    public void Retained_native_views_survive_a_fork_activating_a_precompile([Values] bool movedRepository)
    {
        Address address = Address.FromNumber(0x0b);
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository inner = new(worldState);
        CodeOverrideStore overrides = new();
        OverridableCodeInfoRepository repository = new(inner, worldState, overrides, new(disableOnAccountOverride: false));
        worldState.ApplyStateOverridesNoCommit(repository, new() { [address] = new() { Code = [(byte)Instruction.STOP] } }, Cancun.Instance);

        repository.ResetPrecompileOverrides(Prague.Instance);
        ICodeInfoRepository reader = movedRepository ? new MovedPrecompileCodeInfoRepository(inner, worldState, overrides) : repository;
        CodeInfo dispatch = reader.GetCachedCodeInfo(address, Prague.Instance);
        CodeInfo plain = reader.GetDelegatedCodeInfo(address, Prague.Instance);
        repository.ResetPrecompileOverrides(Prague.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dispatch.IsPrecompile, Is.True);
            Assert.That(dispatch.CodeSpan, Is.SequenceEqualTo(new byte[] { (byte)Instruction.STOP }));
            Assert.That(plain.IsPrecompile, Is.False);
            Assert.That(plain.CodeSpan.SequenceEqual(dispatch.CodeSpan), Is.True);
            Assert.That(reader.GetCachedCodeInfo(address, Prague.Instance), Is.SameAs(dispatch));
            Assert.That(reader.GetDelegatedCodeInfo(address, Prague.Instance), Is.SameAs(plain));
        }
    }

    [Test]
    public void Explicit_move_keeps_origin_code_and_destination_native_code_views(
        [Values] bool preservePrecompiles, [Values] bool movedRepository)
    {
        IReleaseSpec spec = Prague.Instance;
        Address source = IdentityPrecompile.Address;
        Address destination = TestItem.AddressA;
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository inner = new(worldState);
        CodeOverrideStore overrides = new();
        OverridableCodeInfoRepository repository = new(inner, worldState, overrides, new(disableOnAccountOverride: !preservePrecompiles));
        worldState.ApplyStateOverridesNoCommit(repository, new() { [destination] = new() { Code = [(byte)Instruction.STOP] } }, spec);
        if (preservePrecompiles)
            worldState.ApplyStateOverridesNoCommit(repository, new() { [source] = new() { Code = [(byte)Instruction.INVALID] } }, spec);
        worldState.ApplyStateOverridesNoCommit(repository, new()
        {
            [source] = new() { MovePrecompileToAddress = destination, Code = [(byte)Instruction.RETURN] }
        }, spec);
        ICodeInfoRepository reader = movedRepository ? new MovedPrecompileCodeInfoRepository(inner, worldState, overrides) : repository;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetCachedCodeInfo(source, spec).IsPrecompile, Is.False);
            Assert.That(reader.GetCachedCodeInfo(source, spec).CodeSpan, Is.SequenceEqualTo(new byte[] { (byte)Instruction.RETURN }));
            Assert.That(reader.GetPrecompile(source, spec), Is.Null);
            Assert.That(reader.GetCachedCodeInfo(destination, spec).Precompile, Is.SameAs(IdentityPrecompile.Instance));
            Assert.That(reader.GetCachedCodeInfo(destination, spec).CodeSpan, Is.SequenceEqualTo(new byte[] { (byte)Instruction.STOP }));
            Assert.That(reader.GetDelegatedCodeInfo(destination, spec).IsPrecompile, Is.False);
            Assert.That(reader.GetDelegatedCodeInfo(destination, spec).CodeSpan, Is.SequenceEqualTo(new byte[] { (byte)Instruction.STOP }));
        }
    }

    /// <summary>
    /// A move destination is an ordinary account, so an EIP-7702 authorization it signs after the move changes the
    /// code it shows while the moved precompile keeps running there.
    /// </summary>
    [Test]
    public void Move_destination_code_view_follows_a_later_authorization(
        [Values] bool preservePrecompiles, [Values] bool movedRepository, [Values] bool clearDelegation)
    {
        IReleaseSpec spec = Prague.Instance;
        Address destination = TestItem.AddressA;
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository inner = new(worldState);
        CodeOverrideStore overrides = new();
        OverridableCodeInfoRepository repository = new(inner, worldState, overrides, new(disableOnAccountOverride: !preservePrecompiles));
        worldState.CreateAccount(destination, 0);
        inner.SetDelegation(TestItem.AddressB, destination, spec);
        worldState.ApplyStateOverridesNoCommit(repository, new()
        {
            [IdentityPrecompile.Address] = new() { MovePrecompileToAddress = destination }
        }, spec);
        ICodeInfoRepository reader = movedRepository ? new MovedPrecompileCodeInfoRepository(inner, worldState, overrides) : repository;
        Assert.That(reader.GetCachedCodeInfo(destination, spec).CodeSpan, Is.SequenceEqualTo(worldState.GetCode(destination).Span));

        reader.SetDelegation(clearDelegation ? Address.Zero : TestItem.AddressC, destination, spec);
        CodeInfo dispatch = reader.GetCachedCodeInfo(destination, spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dispatch.Precompile, Is.SameAs(IdentityPrecompile.Instance));
            Assert.That(dispatch.CodeSpan, Is.SequenceEqualTo(worldState.GetCode(destination).Span));
            Assert.That(dispatch.CodeSpan.IsEmpty, Is.EqualTo(clearDelegation));
            Assert.That(reader.GetPrecompile(destination, spec), Is.SameAs(IdentityPrecompile.Instance));
            if (!movedRepository) Assert.That(reader.GetCachedCodeInfo(destination, spec), Is.SameAs(dispatch), "the refreshed view is cached");
        }
    }

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
        IWorldState inner = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = inner.BeginScope(IWorldState.PreGenesis);
        AccessRecordingWorldState worldState = new(inner);
        CodeOverrideStore overrides = new();
        overrides.Precompiles[movedTo] = (new CodeInfo(IdentityPrecompile.Instance), worldState.GetCodeHash(movedTo));
        MovedPrecompileCodeInfoRepository repository = new(Substitute.For<ICodeInfoRepository>(), worldState, overrides);

        CodeInfo codeInfo = repository.GetCachedCodeInfo(movedTo, followDelegation: false, Prague.Instance, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(codeInfo.IsPrecompile, Is.True);
            Assert.That(worldState.Reads, Is.EqualTo(new[] { movedTo }));
            Assert.That(worldState.Accesses, Is.EqualTo(new[] { movedTo }));
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

    private sealed class AccessRecordingWorldState(IWorldState state) : WorldStateDecorator(state)
    {
        public List<Address> Reads { get; } = [];
        public List<Address> Accesses { get; } = [];

        public override void AddAccountRead(Address address)
        {
            Reads.Add(address);
            base.AddAccountRead(address);
        }

        public override void RecordAccountAccess(Address address)
        {
            Accesses.Add(address);
            base.RecordAccountAccess(address);
        }
    }
}
