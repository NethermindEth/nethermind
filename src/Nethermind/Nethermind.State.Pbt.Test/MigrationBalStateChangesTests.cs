// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.Specs.Forks;
using Nethermind.State.Pbt.Migration;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class MigrationBalStateChangesTests
{
    private static readonly string FixtureDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Eip8347");

    [Test]
    public async Task Final_deltas_match_independent_geth_roots(
        [Values("a1", "a2", "a3", "a4", "a5", "b2", "b3", "b4", "b5", "b6")] string name,
        [Values] bool binary)
    {
        using JsonDocument blocks = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "blocks.json")));
        JsonElement block = FindBlock(blocks, "name", name);
        JsonElement parent = FindBlock(blocks, "blockHash", block.GetProperty("parentHash").GetString()!);
        await using IContainer container = CreateContainer(binary);
        await using ILifetimeScope environment = CreateEnvironment(container);
        IWorldState worldState = environment.Resolve<IWorldState>();
        using JsonDocument parentAllocations = LoadAllocations(parent.GetProperty("name").GetString()!);
        Hash256 parentRoot;
        using (worldState.BeginScope(null))
        {
            Seed(worldState, parentAllocations.RootElement);
            worldState.Commit(Amsterdam.Instance, isGenesis: true);
            worldState.CommitTree(0);
            parentRoot = worldState.StateRoot;
        }
        Assert.That(parentRoot, Is.EqualTo(new Hash256(parent.GetProperty(binary ? "pbtRoot" : "mptRoot").GetString()!)), "independent parent root");

        using (worldState.BeginScope(Build.A.BlockHeader.WithNumber(0).WithStateRoot(parentRoot).TestObject))
        {
            MigrationBalStateChanges.Apply(Decode(block.GetProperty("balRlp").GetString()!), worldState, Amsterdam.Instance);
            worldState.RecalculateStateRoot();
            using JsonDocument allocations = LoadAllocations(name);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(worldState.StateRoot, Is.EqualTo(new Hash256(block.GetProperty(binary ? "pbtRoot" : "mptRoot").GetString()!)), name);
                AssertAllocation(worldState, allocations.RootElement);
            }
        }
    }

    [Test]
    public async Task Applies_only_final_values_and_deletes_empty_final_accounts([Values] bool binary)
    {
        await using IContainer container = CreateContainer(binary);
        await using ILifetimeScope environment = CreateEnvironment(container);
        IWorldState worldState = environment.Resolve<IWorldState>();
        Hash256 parentRoot;
        byte[] code = Bytes.FromHexString("600160005500");
        using (worldState.BeginScope(null))
        {
            worldState.CreateAccount(TestItem.AddressA, 20, 1);
            worldState.Set(new StorageCell(TestItem.AddressA, 1), 0xff);
            worldState.CreateAccount(TestItem.AddressB, 30, 1);
            worldState.InsertCode(TestItem.AddressB, code, Amsterdam.Instance);
            worldState.Set(new StorageCell(TestItem.AddressB, 2), 0xab);
            worldState.Commit(Amsterdam.Instance, isGenesis: true);
            worldState.CommitTree(0);
            parentRoot = worldState.StateRoot;
        }
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA)
                .WithBalanceChanges(new BalanceChange(1, 100), new BalanceChange(2, 0))
                .WithNonceChanges(new NonceChange(1, 2), new NonceChange(2, 0))
                .WithCodeChanges(new CodeChange(2, []))
                .WithStorageChanges(1, new StorageChange(2, 0u)).TestObject)
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressB)
                .WithBalanceChanges(new BalanceChange(1, 10), new BalanceChange(2, 40))
                .WithStorageReads(2)
                .WithStorageChanges(1, new StorageChange(1, 255u), new StorageChange(2, 42u)).TestObject)
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressC)
                .WithBalanceChanges(new BalanceChange(1, 25)).TestObject)
            .TestObject;
        using (worldState.BeginScope(Build.A.BlockHeader.WithNumber(0).WithStateRoot(parentRoot).TestObject))
        {
            MigrationBalStateChanges.Apply(bal, worldState, Amsterdam.Instance);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(worldState.AccountExists(TestItem.AddressA), Is.False);
                Assert.That(worldState.GetBalance(TestItem.AddressB), Is.EqualTo((UInt256)40));
                Assert.That(worldState.GetCode(TestItem.AddressB), Is.EqualTo(code));
                Assert.That(worldState.Get(new StorageCell(TestItem.AddressB, 1)), Is.EqualTo((UInt256)0x2a));
                Assert.That(worldState.Get(new StorageCell(TestItem.AddressB, 2)), Is.EqualTo((UInt256)0xab));
                Assert.That(worldState.GetBalance(TestItem.AddressC), Is.EqualTo((UInt256)25));
                Assert.That(worldState.GetCodeHash(TestItem.AddressC), Is.EqualTo(ValueKeccak.OfAnEmptyString));
            }
        }
    }

    private static IContainer CreateContainer(bool binary)
    {
        ContainerBuilder builder = new ContainerBuilder().AddModule(new TestNethermindModule(Amsterdam.Instance));
        if (binary) builder.AddModule(new PbtModule(new PbtConfig { Enabled = true }));
        return builder.Build();
    }

    private static ILifetimeScope CreateEnvironment(IContainer container) => container.BeginLifetimeScope(builder =>
        builder.AddSingleton<IWorldStateScopeProvider>(container.Resolve<IWorldStateManager>().GlobalWorldState));

    private static JsonDocument LoadAllocations(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "states", name + ".alloc.json")));

    private static JsonElement FindBlock(JsonDocument blocks, string property, string value)
    {
        foreach (JsonElement block in blocks.RootElement.EnumerateArray())
            if (block.GetProperty(property).GetString() == value) return block;
        throw new InvalidDataException($"Missing fixture block {value}");
    }

    private static ReadOnlyBlockAccessList Decode(string hex)
    {
        RlpReader reader = new(Bytes.FromHexString(hex));
        return BlockAccessListDecoder.Instance.Decode(ref reader)!;
    }

    private static UInt256 Number(JsonElement account, string property) =>
        account.TryGetProperty(property, out JsonElement value) ? new UInt256(Bytes.FromHexString(value.GetString()!), true) : UInt256.Zero;

    private static void Seed(IWorldState worldState, JsonElement allocations)
    {
        foreach (JsonProperty entry in allocations.EnumerateObject())
        {
            Address address = new(entry.Name);
            worldState.CreateAccount(address, Number(entry.Value, "balance"), (ulong)Number(entry.Value, "nonce"));
            if (entry.Value.TryGetProperty("code", out JsonElement code))
                worldState.InsertCode(address, Bytes.FromHexString(code.GetString()!), Amsterdam.Instance, isGenesis: true);
            if (entry.Value.TryGetProperty("storage", out JsonElement storage))
                foreach (JsonProperty slot in storage.EnumerateObject())
                    worldState.Set(new StorageCell(address, new UInt256(Bytes.FromHexString(slot.Name), true)), new UInt256(Bytes.FromHexString(slot.Value.GetString()!), true));
        }
    }

    private static void AssertAllocation(IWorldState worldState, JsonElement allocations)
    {
        foreach (JsonProperty entry in allocations.EnumerateObject())
        {
            Address address = new(entry.Name);
            byte[] code = entry.Value.TryGetProperty("code", out JsonElement codeValue) ? Bytes.FromHexString(codeValue.GetString()!) : [];
            Assert.That(worldState.GetBalance(address), Is.EqualTo(Number(entry.Value, "balance")), entry.Name);
            Assert.That(worldState.GetNonce(address), Is.EqualTo((ulong)Number(entry.Value, "nonce")), entry.Name);
            Assert.That(worldState.GetCodeHash(address), Is.EqualTo(ValueKeccak.Compute(code)), entry.Name);
            Assert.That(worldState.GetCode(address), Is.EqualTo(code), entry.Name);
        }
    }
}
