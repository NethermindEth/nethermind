// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class GuestWorldStateTests
{
    private static readonly Address[] Addresses =
    [
        TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE, TestItem.AddressF,
    ];

    private static readonly byte[][] Codes = [[1, 2, 3], [0x60, 0x00, 0x60, 0x00], [7]];

    private static IEnumerable<IReleaseSpec> Specs() => [Frontier.Instance, SpuriousDragon.Instance, Cancun.Instance];

    /// <summary>Drives a <see cref="GuestWorldState"/> and a <see cref="WorldState"/> through the same random operations.</summary>
    /// <remarks>
    /// Every read, every thrown exception and every state root must agree. The operations follow what execution does:
    /// where it never goes, <see cref="WorldState"/> has quirks that <see cref="GuestWorldState"/> need not share.
    /// </remarks>
    [Test]
    public void Matches_world_state([Range(0, 299)] int seed, [ValueSource(nameof(Specs))] IReleaseSpec spec)
    {
        (IWorldState expected, BlockHeader baseBlock) = CreatePreState(guest: false);
        (IWorldState actual, _) = CreatePreState(guest: true);

        using IDisposable expectedScope = expected.BeginScope(baseBlock);
        using IDisposable actualScope = actual.BeginScope(baseBlock);

        Random random = new(seed);
        List<(Snapshot Expected, Snapshot Actual)> snapshots = [];
        List<string> log = [];
        // Accounts whose storage the commit window cleared, and those of them with a revert since. Execution reads no
        // original past a reverted clear, and the two states differ there.
        HashSet<Address> cleared = [];
        HashSet<Address> clearReverted = [];
        for (int step = 0; step < 400; step++)
        {
            Address address = Addresses[random.Next(Addresses.Length)];
            StorageCell cell = new(Addresses[random.Next(3)], (UInt256)random.Next(5));
            UInt256 value = random.Next(3) == 0 ? UInt256.Zero : (UInt256)random.Next(1, 4);
            int operation = random.Next(26);
            log.Add($"{step}: {operation} {address} {cell} {value}");

            // Storage is read and written only in the context of a contract: an account with code, or with nonce 1
            // while its constructor runs. Neither is ever empty.
            if (operation is 5 or 6 or 7 or 23)
            {
                bool isContext = expected.IsNonZeroAccount(cell.Address, out _);
                Assert.That(actual.IsNonZeroAccount(cell.Address, out _), Is.EqualTo(isContext), Trace);
                if (!isContext) continue;
            }

            switch (operation)
            {
                case 0 or 1:
                    snapshots.Add((expected.TakeSnapshot(), actual.TakeSnapshot()));
                    break;
                case 2:
                    // A block builder restores to a transaction start, never past it.
                    snapshots.Clear();
                    snapshots.Add((expected.TakeSnapshot(newTransactionStart: true), actual.TakeSnapshot(newTransactionStart: true)));
                    break;
                case 3 when snapshots.Count != 0:
                    {
                        int index = random.Next(snapshots.Count);
                        log.Add($"restore {index}");
                        expected.Restore(snapshots[index].Expected);
                        actual.Restore(snapshots[index].Actual);
                        snapshots.RemoveRange(index + 1, snapshots.Count - index - 1);
                        clearReverted.UnionWith(cleared);
                        break;
                    }
                case 4:
                    {
                        bool commitRoots = random.Next(4) == 0;
                        log.Add($"commit roots {commitRoots}");
                        expected.Commit(spec, NullStateTracer.Instance, commitRoots: commitRoots);
                        actual.Commit(spec, NullStateTracer.Instance, commitRoots: commitRoots);
                        snapshots.Clear();
                        cleared.Clear();
                        clearReverted.Clear();
                        if (random.Next(2) == 0)
                        {
                            expected.ResetTransient();
                            actual.ResetTransient();
                        }

                        if (commitRoots) AssertSameRoot();
                        break;
                    }
                case 5:
                    {
                        bool compareOriginal = !clearReverted.Contains(cell.Address);
                        Compare(s =>
                        {
                            s.Get(cell, out UInt256 current);
                            s.GetOriginal(cell, out UInt256 original);
                            return compareOriginal ? $"{current} {original}" : $"{current}";
                        });
                        break;
                    }
                case 6 or 7:
                    {
                        bool compareOriginal = !clearReverted.Contains(cell.Address);
                        Compare(s =>
                        {
                            s.Get(cell, out UInt256 current);
                            s.GetOriginal(cell, out UInt256 original);
                            s.Set(cell, value, current);
                            return compareOriginal ? $"{current} {original}" : $"{current}";
                        });
                        break;
                    }
                case 8:
                    Compare(s => { s.GetTransientState(cell, out UInt256 v); return v.ToString(); });
                    break;
                case 9:
                    Compare(s => { s.SetTransientState(cell, value); return ""; });
                    break;
                case 10:
                    Compare(s => Describe(s, address));
                    break;
                case 11:
                    Compare(s => { s.AddToBalance(address, value, spec, out UInt256 old); return old.ToString(); });
                    break;
                case 12:
                    Compare(s => { s.SubtractFromBalance(address, value, spec, out UInt256 old); return old.ToString(); });
                    break;
                case 13:
                    Compare(s => { s.IncrementNonce(address, 1, out ulong old); return old.ToString(); });
                    break;
                case 14:
                    {
                        ulong nonce = (ulong)random.Next(2);
                        // Execution creates only an account that does not exist, which it reads first.
                        Compare(s =>
                        {
                            bool existed = s.AccountExists(address);
                            if (!existed) s.CreateAccount(address, value, nonce);
                            return existed.ToString();
                        });
                        break;
                    }
                case 15:
                    // Execution clears an account's storage before it deletes the account.
                    Compare(s => { s.ClearStorage(address); s.DeleteAccount(address); return ""; });
                    cleared.Add(address);
                    break;
                case 16:
                    Compare(s => { s.ClearStorage(cell.Address); return ""; });
                    cleared.Add(cell.Address);
                    break;
                case 17:
                    {
                        byte[] code = Codes[random.Next(Codes.Length)];
                        Compare(s => { s.InsertCode(address, Keccak.Compute(code), code, spec); return ""; });
                        break;
                    }
                case 18:
                    Compare(s => s.GetOriginalBalance(address).ToString());
                    break;
                case 19:
                    Compare(s => { bool created = s.AddToBalanceAndCreateIfNotExists(address, value, spec, out UInt256 old); return $"{created} {old}"; });
                    break;
                case 20:
                    Compare(s => { bool nonZero = s.IsNonZeroAccount(address, out bool exists); return $"{nonZero} {exists}"; });
                    break;
                case 21:
                    Compare(s => { s.CreateAccountIfNotExists(address, value); return ""; });
                    break;
                case 22:
                    Compare(s => { s.SubtractFromBalanceAndSetNonce(address, value, 5, spec); return ""; });
                    break;
                case 23:
                    // As EIP-7906's storage assertion reads it: the value before a clear, else the live value.
                    Compare(s =>
                    {
                        s.Get(cell, out UInt256 live);
                        return (s.TryGetStorageBeforeClear(cell, out UInt256 before) ? before : live).ToString();
                    });
                    break;
                case 24:
                    Compare(s => s.GetCode(address).ToArray().ToHexString());
                    break;
                case 25:
                    Compare(s => { s.DecrementNonce(address, 1); return ""; });
                    break;
            }
        }

        expected.Commit(spec, NullStateTracer.Instance, commitRoots: true);
        actual.Commit(spec, NullStateTracer.Instance, commitRoots: true);
        AssertSameRoot();

        void AssertSameRoot()
        {
            expected.RecalculateStateRoot();
            actual.RecalculateStateRoot();
            Assert.That(actual.StateRoot, Is.EqualTo(expected.StateRoot), Trace);
        }

        void Compare(Func<IWorldState, string> operation) =>
            Assert.That(Apply(actual, operation), Is.EqualTo(Apply(expected, operation)), Trace);

        string Trace() => string.Join(Environment.NewLine, log);
    }

    private static string Apply(IWorldState state, Func<IWorldState, string> operation)
    {
        try
        {
            return operation(state);
        }
        catch (Exception e)
        {
            return $"throws {e.GetType().Name}";
        }
    }

    private static string Describe(IWorldState state, Address address) =>
        $"{state.AccountExists(address)} {state.IsDeadAccount(address)} {state.IsContract(address)} {state.GetNonce(address)} " +
        $"{state.GetBalance(address)} {state.GetCodeHash(address)}";

    /// <remarks>A, B and C exist, A and B with code and storage; D, E and F do not.</remarks>
    private static (IWorldState State, BlockHeader Base) CreatePreState(bool guest)
    {
        TrieStoreScopeProvider scopeProvider = new(
            new TestRawTrieStore(new NodeStorage(new MemDb())), new MemDb(), UnavailableStateHeaderProvider.Instance, LimboLogs.Instance);
        WorldState builder = new(scopeProvider, LimboLogs.Instance);

        BlockHeader baseBlock;
        using (builder.BeginScope(IWorldState.PreGenesis))
        {
            for (int i = 0; i < 3; i++)
            {
                builder.CreateAccount(Addresses[i], (UInt256)(i * 3), (ulong)(i % 2));
            }

            for (int i = 0; i < 2; i++)
            {
                builder.InsertCode(Addresses[i], Keccak.Compute(Codes[i]), Codes[i], Cancun.Instance);
                for (int slot = 0; slot < 4; slot++)
                {
                    builder.Set(new StorageCell(Addresses[i], (UInt256)slot), (UInt256)(slot + i + 1));
                }
            }

            builder.Commit(Cancun.Instance);
            builder.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithNumber(0).WithStateRoot(builder.StateRoot).TestObject;
        }

        return (guest ? new GuestWorldState(scopeProvider) : builder, baseBlock);
    }
}
