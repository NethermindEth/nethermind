// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Autofac;
using BenchmarkDotNet.Attributes;
using DotNetty.Common.Utilities;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Benchmarks.Store;

public class WorldStateBenchmarks
{
    private IContainer _container;
    private IWorldState _globalWorldState;

    private const int _accountCount = 1024 * 4;
    private const int _contractCount = 128;
    private const int _slotsCount = _contractCount * 128;
    private const int _bigContractSlotsCount = 1024 * 4;
    private const int _loopSize = 1024 * 10;

    private Address[] _accounts;
    private Address[] _contracts;
    private (Address Account, UInt256 Slot)[] _slots;
    private Address _bigContract;
    private UInt256[] _bigContractSlots;
    private IReleaseSpec _releaseSpec = new Prague();
    private BlockHeader _baseBlock;

    [GlobalSetup]
    public void Setup()
    {
        // Note: The whole thing is in pruning cache, so the KV db is not touched in this benchmark.
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .Build();

        IWorldState worldState = _globalWorldState = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        using var _ = worldState.BeginScope(IWorldState.PreGenesis);

        Random rand = new Random(0);
        byte[] randomBuffer = new byte[20];
        _accounts = new Address[_accountCount];
        for (int i = 0; i < _accountCount; i++)
        {
            rand.NextBytes(randomBuffer);
            Address account = new Address(randomBuffer.ToArray());
            worldState.AddToBalanceAndCreateIfNotExists(account, (UInt256)rand.NextLong() + 1, _releaseSpec);
            _accounts[i] = account;
        }

        _contracts = new Address[_contractCount];
        for (int i = 0; i < _contractCount; i++)
        {
            rand.NextBytes(randomBuffer);
            Address account = new Address(randomBuffer.ToArray());
            worldState.AddToBalanceAndCreateIfNotExists(account, (UInt256)rand.NextLong() + 1, _releaseSpec);
            _contracts[i] = account;
        }

        _slots = new (Address, UInt256)[_slotsCount];
        for (int i = 0; i < _slotsCount; i++)
        {
            Address account = _contracts[rand.Next(0, _contracts.Length)];
            UInt256 slot = (UInt256)rand.NextLong();
            rand.NextBytes(randomBuffer);
            worldState.Set(new StorageCell(account, slot), randomBuffer.ToArray());
            _slots[i] = (account, slot);
        }

        rand.NextBytes(randomBuffer);
        _bigContract = new Address(randomBuffer.ToArray());
        worldState.AddToBalanceAndCreateIfNotExists(_bigContract, 1, _releaseSpec);
        _bigContractSlots = new UInt256[_bigContractSlotsCount];
        for (int i = 0; i < _bigContractSlotsCount; i++)
        {
            UInt256 slot = (UInt256)rand.NextLong();
            rand.NextBytes(randomBuffer);
            _bigContractSlots[i] = slot;
            worldState.Set(new StorageCell(_bigContract, slot), randomBuffer.ToArray());
        }

        worldState.Commit(_releaseSpec);
        worldState.CommitTree(0);
        worldState.Reset();
        _baseBlock = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
    }

    [GlobalCleanup]
    public void Teardown()
    {
        _container.Dispose();
    }

    [Benchmark]
    public void AccountRead()
    {
        Random rand = new Random(1);
        IWorldState worldState = _globalWorldState;
        using var _ = worldState.BeginScope(_baseBlock);

        for (int i = 0; i < _loopSize; i++)
        {
            worldState.GetBalance(_accounts[rand.Next(0, _accounts.Length)]);
        }

        worldState.Reset();
    }

    [Benchmark]
    public void AccountReadWrite()
    {
        Random rand = new Random(1);
        IWorldState worldState = _globalWorldState;
        using var _ = worldState.BeginScope(_baseBlock);

        for (int i = 0; i < _loopSize; i++)
        {
            if (rand.NextDouble() < 0.5)
            {
                worldState.GetBalance(_accounts[rand.Next(0, _accounts.Length)]);
            }
            else
            {
                worldState.AddToBalance(_accounts[rand.Next(0, _accounts.Length)], 1, _releaseSpec);
            }
        }

        worldState.Commit(_releaseSpec);
        worldState.CommitTree(1);
        worldState.Reset();
    }

    [Benchmark]
    public void SlotRead()
    {
        Random rand = new Random(1);
        IWorldState worldState = _globalWorldState;
        using var _ = worldState.BeginScope(_baseBlock);

        for (int i = 0; i < _loopSize; i++)
        {
            (Address Account, UInt256 Slot) slot = _slots[rand.Next(0, _slots.Length)];
            worldState.Get(new StorageCell(slot.Account, slot.Slot));
        }

        worldState.Reset();
    }

    [Benchmark]
    public void SlotReadWrite()
    {
        Random rand = new Random(1);
        IWorldState worldState = _globalWorldState;
        using var _ = worldState.BeginScope(_baseBlock);
        byte[] randomBuffer = new byte[20];

        for (int i = 0; i < _loopSize; i++)
        {
            (Address Account, UInt256 Slot) slot = _slots[rand.Next(0, _slots.Length)];
            if (rand.NextDouble() < 0.5)
            {
                worldState.Get(new StorageCell(slot.Account, slot.Slot));
            }
            else
            {
                rand.NextBytes(randomBuffer);
                worldState.Set(new StorageCell(slot.Account, slot.Slot), randomBuffer.ToArray());
            }
        }

        worldState.Commit(_releaseSpec);
        worldState.CommitTree(1);
        worldState.Reset();
    }

    [Benchmark]
    public void SameContractRead()
    {
        Random rand = new Random(1);
        IWorldState worldState = _globalWorldState;
        using var _ = worldState.BeginScope(_baseBlock);

        for (int i = 0; i < _loopSize; i++)
        {
            UInt256 slot = _bigContractSlots[rand.Next(0, _bigContractSlots.Length)];
            worldState.Get(new StorageCell(_bigContract, slot));
        }

        worldState.Reset();
    }

    [Benchmark]
    public void SameContractReadWrite()
    {
        Random rand = new Random(1);
        IWorldState worldState = _globalWorldState;
        using var _ = worldState.BeginScope(_baseBlock);
        byte[] randomBuffer = new byte[20];

        for (int i = 0; i < _loopSize; i++)
        {
            UInt256 slot = _bigContractSlots[rand.Next(0, _bigContractSlots.Length)];
            if (rand.NextDouble() < 0.5)
            {
                worldState.Get(new StorageCell(_bigContract, slot));
            }
            else
            {
                rand.NextBytes(randomBuffer);
                worldState.Set(new StorageCell(_bigContract, slot), randomBuffer.ToArray());
            }
        }

        worldState.Commit(_releaseSpec);
        worldState.CommitTree(1);
        worldState.Reset();
    }
}
