// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Healing;
using Nethermind.Synchronization.FastSync;
using NSubstitute;
using NUnit.Framework;
using EvmMetrics = Nethermind.Evm.Metrics;

namespace Nethermind.State.Flat.Test.Sync;

/// <summary>
/// Applies the same block access lists through <see cref="IWorldState.ApplyBal"/> (catch-up and parallel validation)
/// and through <see cref="FlatBalHealing"/> (snap healing), which must reach the same state.
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
[Parallelizable(ParallelScope.All)]
public class BalApplierDifferentialTests(bool flatScope)
{
    private static readonly byte[] Code = [0x60, 0x00, 0x60, 0x00];
    private static readonly byte[] Replacement = [0x60, 0x01];
    private static readonly Address A = TestItem.AddressA;
    private static readonly Address B = TestItem.AddressB;

    public sealed record AccountSpec(Address Address, UInt256 Balance, ulong Nonce = 0, byte[]? Code = null, (UInt256 Key, UInt256 Value)[]? Slots = null);

    public sealed record Scenario(string Name, AccountSpec[] Pre, ReadOnlyBlockAccessList[] Blocks, AccountSpec[] Post)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<Scenario> Scenarios()
    {
        yield return new("EIP-161 removes an emptied account with its storage",
            [new(A, 100, Slots: [(1, 5)]), new(B, 200)],
            [Bal(Changes(A).WithBalanceChanges(new BalanceChange(1, 0)))],
            [new(B, 200)]);

        yield return new("7702 delegation cleared keeps storage",
            [new(A, 100, Nonce: 1, Code: [.. Eip7702Constants.DelegationHeader, .. B.Bytes], Slots: [(1, 5)])],
            [Bal(Changes(A).WithNonceChanges(new NonceChange(1, 2)).WithCodeChanges(new CodeChange(1, [])))],
            [new(A, 100, Nonce: 2, Slots: [(1, 5)])]);

        yield return new("slot set to zero is removed",
            [new(A, 100, Nonce: 1, Code: Code, Slots: [(1, 5), (2, 7)])],
            [Bal(Changes(A).WithStorageChanges(1, new StorageChange(1, 9), new StorageChange(2, 0)).WithStorageChanges(3, new StorageChange(1, 4)))],
            [new(A, 100, Nonce: 1, Code: Code, Slots: [(2, 7), (3, 4)])]);

        // EIP-8298: the depositor replaces the code later in the block, so only its first change carries the bytes.
        yield return new("adopted code deposited earlier in the block",
            [new(A, 100, Nonce: 1)],
            [Bal(
                Changes(A).WithCodeChanges(CodeChange.Adopted(2, ValueKeccak.Compute(Code))),
                Changes(B).WithBalanceChanges(new BalanceChange(1, 500)).WithNonceChanges(new NonceChange(1, 1))
                    .WithCodeChanges(new CodeChange(1, Code), new CodeChange(3, Replacement)))],
            [new(A, 100, Nonce: 1, Code: Code), new(B, 500, Nonce: 1, Code: Replacement)]);

        yield return new("emptied then revived in one block loses earlier storage",
            [new(A, 100, Slots: [(1, 5)])],
            [Bal(Changes(A)
                .WithBalanceChanges(new BalanceChange(1, 0), new BalanceChange(2, 100))
                .WithNonceChanges(new NonceChange(2, 1))
                .WithCodeChanges(new CodeChange(2, Code))
                .WithStorageChanges(2, new StorageChange(3, 7))
                .WithStorageChanges(3, new StorageChange(1, 8)))],
            [new(A, 100, Nonce: 1, Code: Code, Slots: [(2, 7)])]);

        yield return new("emptied in one block, revived in the next",
            [new(A, 100, Slots: [(1, 5)]), new(B, 1)],
            [Bal(Changes(A).WithBalanceChanges(new BalanceChange(1, 0))), Bal(Changes(A).WithBalanceChanges(new BalanceChange(1, 50)))],
            [new(A, 50), new(B, 1)]);

        // Unreachable post-merge (EIP-7523): a slot write touches an empty account, which EIP-161 then removes.
        yield return new("slot write to an empty account holding storage removes it",
            [new(A, 0, Slots: [(1, 5)]), new(B, 1)],
            [Bal(Changes(A).WithStorageChanges(1, new StorageChange(1, 9)))],
            [new(B, 1)]);

        yield return new("slot-only change to a contract keeps the account",
            [new(A, 0, Nonce: 1, Code: Code, Slots: [(1, 1)])],
            [Bal(Changes(A).WithStorageChanges(1, new StorageChange(0, 2))), Bal(Changes(A).WithStorageChanges(2, new StorageChange(0, 3)))],
            [new(A, 0, Nonce: 1, Code: Code, Slots: [(1, 2), (2, 3)])]);

        yield return new("read-only empty account is left in place",
            [new(A, 0), new(B, 100)],
            [Bal(Changes(A), Changes(B).WithBalanceChanges(new BalanceChange(1, 150)))],
            [new(A, 0), new(B, 150)]);
    }

    [TestCaseSource(nameof(Scenarios))]
    public void Both_appliers_reach_the_expected_state(Scenario scenario)
    {
        Hash256 expected = CommitToNewPersistence(scenario.Post);

        Hash256[] scopeRoots = ApplyThroughScope(scenario.Pre, scenario.Blocks, out Hash256 preRoot, out IReadOnlyDictionary<Address, byte[]> scopeCode);
        (Hash256? chunked, MemDb chunkedCode) = ApplyThroughHealing(scenario.Pre, scenario.Blocks, scopeRoots, blockByBlock: false);
        (Hash256? blockByBlock, _) = ApplyThroughHealing(scenario.Pre, scenario.Blocks, scopeRoots, blockByBlock: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preRoot, Is.EqualTo(CommitToNewPersistence(scenario.Pre)), "both start from the same state");
            Assert.That(scopeRoots[^1], Is.EqualTo(expected), "ApplyBal");
            Assert.That(chunked, Is.EqualTo(expected), "healing, one chunk");
            Assert.That(blockByBlock, Is.EqualTo(expected), "healing, block by block");
            foreach (AccountSpec account in scenario.Post)
            {
                if (account.Code is not { } code || !scopeCode.ContainsKey(account.Address)) continue;
                Assert.That(scopeCode[account.Address], Is.EqualTo(code), $"ApplyBal code of {account.Address}");
                Assert.That(chunkedCode.Get(Keccak.Compute(code).Bytes), Is.EqualTo(code), $"healing code of {account.Address}");
            }
        }
    }

    [Test]
    public void Adopted_code_missing_everywhere_is_reported_and_both_set_the_hash()
    {
        AccountSpec[] pre = [new(A, 100, Nonce: 1)];
        ReadOnlyBlockAccessList[] blocks = [Bal(Changes(A).WithCodeChanges(CodeChange.Adopted(1, ValueKeccak.Compute(Code))))];
        long missingBefore = EvmMetrics.BalAdoptedCodeMissing;

        Hash256[] scopeRoots = ApplyThroughScope(pre, blocks, out _, out _, readCode: false);
        (Hash256? healed, _) = ApplyThroughHealing(pre, blocks, scopeRoots, blockByBlock: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scopeRoots[^1], Is.EqualTo(CommitToNewPersistence([new(A, 100, Nonce: 1, Code: Code)])));
            Assert.That(healed, Is.EqualTo(scopeRoots[^1]));
            Assert.That(EvmMetrics.BalAdoptedCodeMissing, Is.GreaterThan(missingBefore));
        }
    }

    /// <summary>
    /// Replays EEST engine fixtures: each valid payload's list is applied to the state left by the previous one and
    /// must reproduce the payload's state root through both paths.
    /// </summary>
    [Test]
    [Explicit("Needs EEST engine fixtures in the directory named by EEST_FIXTURES_DIR")]
    public void Eest_block_access_lists_reach_the_payload_state_roots()
    {
        string? directory = Environment.GetEnvironmentVariable("EEST_FIXTURES_DIR");
        if (string.IsNullOrEmpty(directory)) Assert.Ignore("EEST_FIXTURES_DIR is not set");

        int tests = 0, blocks = 0, skipped = 0;
        List<string> failures = [];
        foreach (string file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Order())
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (JsonProperty test in document.RootElement.EnumerateObject())
            {
                if (!TryReadFixture(test.Value, out AccountSpec[] pre, out Hash256 genesisRoot, out ReadOnlyBlockAccessList[] bals, out Hash256[] expected))
                {
                    skipped++;
                    continue;
                }

                tests++;
                blocks += bals.Length;
                string name = $"{Path.GetFileName(file)}: {test.Name}";
                try
                {
                    Hash256[] scopeRoots = ApplyThroughScope(pre, bals, out Hash256 preRoot, out _, readCode: false);
                    if (preRoot != genesisRoot)
                    {
                        failures.Add($"{name}: pre-state root {preRoot}, genesis {genesisRoot}");
                        continue;
                    }

                    for (int i = 0; i < bals.Length; i++)
                    {
                        if (scopeRoots[i] != expected[i]) failures.Add($"{name}: ApplyBal of block {i + 1} gives {scopeRoots[i]}, expected {expected[i]}");
                    }

                    if (ApplyThroughHealing(pre, bals, scopeRoots, blockByBlock: true).Root is null) failures.Add($"{name}: healing block by block differs from ApplyBal");
                    if (ApplyThroughHealing(pre, bals, scopeRoots, blockByBlock: false).Root is null) failures.Add($"{name}: healing in one chunk differs from ApplyBal");
                }
                catch (Exception e)
                {
                    failures.Add($"{name}: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        TestContext.Out.WriteLine($"{tests} tests, {blocks} blocks applied, {skipped} skipped, {failures.Count} failures");
        foreach (string failure in failures.Take(50)) TestContext.Out.WriteLine(failure);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tests, Is.GreaterThan(0), "no fixtures applied");
            Assert.That(failures, Is.Empty);
        }
    }

    /// <summary>Reads the pre-state and the lists of the valid payloads that extend the genesis block one by one.</summary>
    private static bool TryReadFixture(JsonElement test, out AccountSpec[] pre, out Hash256 genesisRoot,
        out ReadOnlyBlockAccessList[] bals, out Hash256[] expected)
    {
        pre = [];
        bals = [];
        expected = [];
        genesisRoot = Keccak.EmptyTreeHash;
        if (!test.TryGetProperty("engineNewPayloads", out JsonElement payloads)) return false;

        JsonElement genesis = test.GetProperty("genesisBlockHeader");
        genesisRoot = new Hash256(genesis.GetProperty("stateRoot").GetString()!);
        string parent = genesis.GetProperty("hash").GetString()!;
        List<ReadOnlyBlockAccessList> lists = [];
        List<Hash256> roots = [];
        foreach (JsonElement payload in payloads.EnumerateArray())
        {
            if (payload.TryGetProperty("validationError", out JsonElement error) && error.ValueKind == JsonValueKind.String) continue;

            JsonElement block = payload.GetProperty("params")[0];
            // A payload off the applied chain belongs to a reorg, which only execution follows.
            if (!string.Equals(block.GetProperty("parentHash").GetString(), parent, StringComparison.OrdinalIgnoreCase)) break;
            if (!block.TryGetProperty("blockAccessList", out JsonElement bal) || bal.ValueKind != JsonValueKind.String) break;

            lists.Add(Rlp.Decode<ReadOnlyBlockAccessList>(new Rlp(Bytes.FromHexString(bal.GetString()!)))!);
            roots.Add(new Hash256(block.GetProperty("stateRoot").GetString()!));
            parent = block.GetProperty("blockHash").GetString()!;
        }

        if (lists.Count == 0) return false;

        List<AccountSpec> accounts = [];
        foreach (JsonProperty account in test.GetProperty("pre").EnumerateObject())
        {
            List<(UInt256, UInt256)> slots = [];
            foreach (JsonProperty slot in account.Value.GetProperty("storage").EnumerateObject())
            {
                UInt256 value = Word(slot.Value.GetString()!);
                if (!value.IsZero) slots.Add((Word(slot.Name), value));
            }

            byte[] code = Bytes.FromHexString(account.Value.GetProperty("code").GetString()!);
            accounts.Add(new AccountSpec(new Address(account.Name), Word(account.Value.GetProperty("balance").GetString()!),
                (ulong)Word(account.Value.GetProperty("nonce").GetString()!), code.Length == 0 ? null : code, [.. slots]));
        }

        pre = [.. accounts];
        bals = [.. lists];
        expected = [.. roots];
        return true;

        static UInt256 Word(string hex) => new(Bytes.FromHexString(hex), isBigEndian: true);
    }

    /// <returns>The state root after each list.</returns>
    /// <param name="code">The final code of each account a list changed the code of, when <paramref name="readCode"/>.</param>
    private Hash256[] ApplyThroughScope(AccountSpec[] pre, ReadOnlyBlockAccessList[] bals, out Hash256 preRoot, out IReadOnlyDictionary<Address, byte[]> code, bool readCode = true)
    {
        IContainer? container = null;
        IWorldState state;
        if (flatScope)
        {
            (IWorldStateScopeProvider scopeProvider, container) = TestWorldStateFactory.CreateFlatScopeProvider(UnavailableStateHeaderProvider.Instance);
            state = new WorldState(scopeProvider, LimboLogs.Instance);
        }
        else
        {
            state = TestWorldStateFactory.CreateForTest();
        }

        using (container)
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            foreach (AccountSpec account in pre)
            {
                state.CreateAccount(account.Address, account.Balance, account.Nonce);
                if (account.Code is { } bytes) state.InsertCode(account.Address, bytes, Frontier.Instance);
                foreach ((UInt256 key, UInt256 value) in account.Slots ?? []) state.Set(new StorageCell(account.Address, key), value);
            }

            // Frontier predates EIP-161, so empty pre-state accounts stay as the fixture has them.
            state.Commit(Frontier.Instance, isGenesis: true);
            state.CommitTree(0);
            preRoot = state.StateRoot;

            Hash256[] roots = new Hash256[bals.Length];
            for (int i = 0; i < bals.Length; i++)
            {
                state.ApplyBal(bals[i]);
                state.RecalculateStateRoot();
                state.Commit(Amsterdam.Instance);
                state.CommitTree(i + 1);
                roots[i] = state.StateRoot;
            }

            Dictionary<Address, byte[]> codes = [];
            foreach (ReadOnlyBlockAccessList bal in readCode ? bals : [])
            {
                foreach (ReadOnlyAccountChanges changes in bal.AccountChanges)
                {
                    if (changes.CodeChanges.Length > 0) codes[changes.Address] = state.GetCode(changes.Address).ToArray();
                }
            }

            code = codes;
            return roots;
        }
    }

    /// <param name="targetRoots">The root each block's header commits to; healing reports no root unless it reaches the last.</param>
    private static (Hash256? Root, MemDb CodeDb) ApplyThroughHealing(AccountSpec[] pre, ReadOnlyBlockAccessList[] bals, Hash256[] targetRoots, bool blockByBlock)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> columns = new();
        RocksDbPersistence persistence = new(columns, LimboLogs.Instance, NullTrieNodeLog.Instance);
        Hash256 preRoot = CommitState(persistence, pre);

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        BlockAccessListStore balStore = new(new MemDb());
        BlockHeader[] headers = new BlockHeader[bals.Length + 1];
        headers[0] = Build.A.BlockHeader.WithNumber(0).WithStateRoot(preRoot).TestObject;
        for (int i = 0; i < bals.Length; i++)
        {
            headers[i + 1] = Build.A.BlockHeader.WithNumber((ulong)i + 1).WithParentHash(headers[i].Hash!).WithStateRoot(targetRoots[i]).TestObject;
            blockTree.FindHeader(headers[i + 1].Number).Returns(headers[i + 1]);
            balStore.Insert(headers[i + 1].Number, headers[i + 1].Hash!, bals[i]);
        }

        MemDb codeDb = new();
        FlatBalHealing healing = new(blockTree, balStore, new TrieReassembler(persistence, LimboLogs.Instance), persistence,
            Substitute.For<ITreeSyncStore>(), codeDb, new Lazy<ICodeRecovery>(Substitute.For<ICodeRecovery>()), LimboLogs.Instance);

        Hash256[] storages = [.. pre.Where(static a => a.Slots is { Length: > 0 }).Select(static a => a.Address.ToAccountPath.ToCommitment())];
        Hash256? root = healing.Reassemble(storages, default);
        if (blockByBlock)
        {
            for (int i = 0; i < bals.Length && root is not null; i++) root = healing.ApplyRange(root, headers[i], headers[i + 1], default).Root;
        }
        else if (root is not null)
        {
            root = healing.ApplyRange(root, headers[0], headers[^1], default).Root;
        }

        return (root, codeDb);
    }

    private static Hash256 CommitToNewPersistence(AccountSpec[] accounts)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> columns = new();
        return CommitState(new RocksDbPersistence(columns, LimboLogs.Instance, NullTrieNodeLog.Instance), accounts);
    }

    /// <summary>Writes a consistent flat state (leaves and trie nodes) without code bytes, and returns its root.</summary>
    private static Hash256 CommitState(RocksDbPersistence persistence, AccountSpec[] accounts)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        StateTree stateTree = new(new PersistenceTrieStoreAdapter(reader, batch, enableDoubleWriteCheck: false), LimboLogs.Instance);

        foreach (AccountSpec spec in accounts)
        {
            Account account = new(spec.Nonce, spec.Balance, Keccak.EmptyTreeHash, spec.Code is { } code ? Keccak.Compute(code) : Keccak.OfAnEmptyString);
            if (spec.Slots is { Length: > 0 } slots)
            {
                StorageTree storage = new(
                    new PersistenceStorageTrieStoreAdapter(reader, batch, spec.Address.ToAccountPath.ToCommitment(), enableDoubleWriteCheck: false),
                    LimboLogs.Instance);
                foreach ((UInt256 key, UInt256 value) in slots)
                {
                    storage.Set(key, value.ToBigEndian().WithoutLeadingZeros());
                    batch.SetStorage(spec.Address, key, value);
                }

                storage.Commit();
                account = account.WithChangedStorageRoot(storage.RootHash);
            }

            stateTree.Set(spec.Address, account);
            batch.SetAccount(spec.Address, account);
        }

        stateTree.Commit();
        return stateTree.RootHash;
    }

    private static AccountChangesBuilder Changes(Address address) => Build.An.AccountChanges.WithAddress(address);

    private static ReadOnlyBlockAccessList Bal(params AccountChangesBuilder[] changes) =>
        Build.A.BlockAccessList.WithAccountChanges([.. changes.Select(static c => c.TestObject)]).TestObject;
}
