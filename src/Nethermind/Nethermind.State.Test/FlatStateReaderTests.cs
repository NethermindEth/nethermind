// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture("Standard")]
[TestFixture("Flat")]
[Parallelizable(ParallelScope.All)]
public class FlatStateReaderTests(string backend)
{
    private sealed class Context : IDisposable
    {
        public IWorldState WorldState { get; }
        public IStateReader Reader { get; }
        private readonly IContainer? _container;

        public Context(string backend)
        {
            switch (backend)
            {
                case "Flat":
                    (WorldState, Reader, _container) = TestWorldStateFactory.CreateFlatForTestWithStateReader();
                    break;
                case "Standard":
                    (WorldState, Reader) = TestWorldStateFactory.CreateForTestWithStateReader();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(backend), backend, null);
            }
        }

        public BlockHeader CommitAndCapture(Action<IWorldState> populate, long blockNumber = 0)
        {
            using IDisposable _ = WorldState.BeginScope(IWorldState.PreGenesis);
            populate(WorldState);
            WorldState.Commit(MuirGlacier.Instance);
            WorldState.CommitTree(blockNumber);
            return Build.A.BlockHeader.WithNumber(blockNumber).WithStateRoot(WorldState.StateRoot).TestObject;
        }

        public void Dispose() => _container?.Dispose();
    }

    [Test]
    public void TryGetAccount_ExistingAccount_ReturnsTrue()
    {
        using Context ctx = new(backend);
        Account expected = TestItem.GenerateIndexedAccount(7);
        BlockHeader header = ctx.CommitAndCapture(state => state.CreateAccount(TestItem.AddressA, expected.Balance, expected.Nonce));

        bool result = ctx.Reader.TryGetAccount(header, TestItem.AddressA, out AccountStruct account);

        result.Should().BeTrue();
        account.Balance.Should().Be(expected.Balance);
        account.Nonce.Should().Be(expected.Nonce);
    }

    [Test]
    public void TryGetAccount_NonExistentAccount_ReturnsFalse()
    {
        using Context ctx = new(backend);
        BlockHeader header = ctx.CommitAndCapture(state => state.CreateAccount(TestItem.AddressA, 1, 1));

        // The out parameter is intentionally not asserted: the IStateReader contract leaves it
        // undefined when result is false, and the two implementations diverge
        // (StateReader => AccountStruct.TotallyEmpty, FlatStateReader => default).
        bool result = ctx.Reader.TryGetAccount(header, TestItem.AddressB, out _);

        result.Should().BeFalse();
    }

    [Test]
    public void GetStorage_ExistingSlot_ReturnsValue()
    {
        using Context ctx = new(backend);
        StorageCell cell = new(TestItem.AddressA, (UInt256)42);
        byte[] value = [0xab, 0xcd];
        BlockHeader header = ctx.CommitAndCapture(state =>
        {
            state.CreateAccount(TestItem.AddressA, 1);
            state.Set(cell, value);
        });

        byte[] result = ctx.Reader.GetStorage(header, cell.Address, cell.Index).ToArray();

        result.Should().Equal(value);
    }

    [Test]
    public void GetCode_EmptyHash_ReturnsEmpty()
    {
        using Context ctx = new(backend);

        ctx.Reader.GetCode(Keccak.OfAnEmptyString).Should().BeEmpty();
        ctx.Reader.GetCode(Keccak.OfAnEmptyString.ValueHash256).Should().BeEmpty();
    }

    [Test]
    public void GetCode_KnownCode_ReturnsCode()
    {
        using Context ctx = new(backend);
        byte[] code = [0x60, 0x80];
        ValueHash256 codeHash = ValueKeccak.Compute(code);
        ctx.CommitAndCapture(state =>
        {
            state.CreateAccount(TestItem.AddressA, 1);
            state.InsertCode(TestItem.AddressA, codeHash, code, MuirGlacier.Instance);
        });

        ctx.Reader.GetCode((Hash256)codeHash).Should().Equal(code);
        ctx.Reader.GetCode(codeHash).Should().Equal(code);
    }

    [Test]
    public void HasStateForBlock_CommittedBlock_ReturnsTrue()
    {
        using Context ctx = new(backend);
        BlockHeader header = ctx.CommitAndCapture(state => state.CreateAccount(TestItem.AddressA, 1));

        ctx.Reader.HasStateForBlock(header).Should().BeTrue();
    }
}
