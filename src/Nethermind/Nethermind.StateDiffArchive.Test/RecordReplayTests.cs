// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.StateDiffArchive.Recording;
using Nethermind.StateDiffArchive.Replay;
using Nethermind.StateDiffArchive.Storage;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateDiffArchive.Test;

/// <summary>
/// Drives real state changes through a flat-backed <see cref="WorldState"/> with recording enabled, then
/// replays the recorded diffs into a fresh flat backend via <see cref="ReplayBlockProcessor"/> and asserts
/// every block's recomputed state root matches the recorded one (the replay scope verifies this and throws
/// on mismatch).
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class RecordReplayTests
{
    [Test]
    public void Record_then_replay_reproduces_state_roots()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sds-e2e-{Guid.NewGuid():N}");
        IReleaseSpec spec = Cancun.Instance;
        byte[] code = Bytes.FromHexString("0x60016002600055");
        ValueHash256 codeHash = Keccak.Compute(code).ValueHash256;

        try
        {
            List<(ulong Number, Hash256 Root)> blocks = Record(dir, spec, code, codeHash);
            Assert.That(Directory.GetFiles(dir, "*.diff"), Is.Not.Empty);
            Replay(dir, spec, blocks);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static List<(ulong, Hash256)> Record(string dir, IReleaseSpec spec, byte[] code, ValueHash256 codeHash)
    {
        List<(ulong, Hash256)> blocks = [];
        using StateDiffStore store = MakeStore(dir, recording: true);
        (IWorldStateScopeProvider flatScope, IContainer container) = TestWorldStateFactory.CreateFlatScopeProvider();
        using (container)
        {
            WorldState ws = new(new RecordingScopeProvider(flatScope, store, LimboLogs.Instance), LimboLogs.Instance);

            // Block 1: fund A, deploy contract B with code; give three contracts (B, E, F) storage so the
            // replay exercises its multi-threaded storage path (>= 3 contracts).
            using (ws.BeginScope(null))
            {
                ws.CreateAccount(TestItem.AddressA, 100.Ether);
                ws.CreateAccount(TestItem.AddressB, 1, 1);
                ws.InsertCode(TestItem.AddressB, codeHash, code, spec);
                ws.Set(new StorageCell(TestItem.AddressB, 1), Bytes.FromHexString("0x1234"));
                ws.Set(new StorageCell(TestItem.AddressB, 2), Bytes.FromHexString("0x5678"));
                ws.CreateAccount(TestItem.AddressE, 2, 1);
                ws.Set(new StorageCell(TestItem.AddressE, 7), Bytes.FromHexString("0xaa"));
                ws.Set(new StorageCell(TestItem.AddressE, 8), Bytes.FromHexString("0xbb"));
                ws.CreateAccount(TestItem.AddressF, 3, 1);
                ws.Set(new StorageCell(TestItem.AddressF, 9), Bytes.FromHexString("0xcccc"));
                ws.Commit(spec);
                ws.CommitTree(1);
                blocks.Add((1, ws.StateRoot));
            }

            // Block 2: change A balance, mutate one slot, clear another, add a new slot.
            using (ws.BeginScope(HeaderFor(blocks[0])))
            {
                ws.AddToBalance(TestItem.AddressA, 5.Ether, spec);
                ws.Set(new StorageCell(TestItem.AddressB, 1), Bytes.FromHexString("0x99"));
                ws.Set(new StorageCell(TestItem.AddressB, 2), new byte[] { 0 });
                ws.Set(new StorageCell(TestItem.AddressB, 3), Bytes.FromHexString("0xabcd"));
                ws.Commit(spec);
                ws.CommitTree(2);
                blocks.Add((2, ws.StateRoot));
            }

            // Block 3: delete contract B (clears its storage), bump A again.
            using (ws.BeginScope(HeaderFor(blocks[1])))
            {
                ws.AddToBalance(TestItem.AddressA, 1.Ether, spec);
                ws.DeleteAccount(TestItem.AddressB);
                ws.Commit(spec);
                ws.CommitTree(3);
                blocks.Add((3, ws.StateRoot));
            }

            // Block 4: two per-transaction commits in one block (as a pre-Byzantium block would), rewriting the
            // same slot across both flushes -> recorded as two write batches and replayed in order.
            using (ws.BeginScope(HeaderFor(blocks[2])))
            {
                ws.Set(new StorageCell(TestItem.AddressE, 7), Bytes.FromHexString("0x01"));
                ws.Commit(spec);
                ws.Set(new StorageCell(TestItem.AddressE, 7), Bytes.FromHexString("0x02"));
                ws.AddToBalance(TestItem.AddressA, 1.Ether, spec);
                ws.Commit(spec);
                ws.CommitTree(4);
                blocks.Add((4, ws.StateRoot));
            }
        }
        return blocks;
    }

    private static void Replay(string dir, IReleaseSpec spec, List<(ulong Number, Hash256 Root)> blocks)
    {
        using StateDiffStore store = MakeStore(dir, replay: true);
        (IWorldStateScopeProvider flatScope, IContainer container) = TestWorldStateFactory.CreateFlatScopeProvider();
        using (container)
        {
            ReplayScopeTracker tracker = new();
            ReplayScopeProvider provider = new(flatScope, tracker, LimboLogs.Instance);
            WorldState ws = new(provider, LimboLogs.Instance);

            IBlockProcessor inner = Substitute.For<IBlockProcessor>();
            ReplayBlockProcessor replay = new(inner, store, tracker, LimboLogs.Instance, parallelAccountRead: true);

            BlockHeader? parent = null;
            foreach ((ulong number, Hash256 root) in blocks)
            {
                Block suggested = Build.A.Block.WithNumber(number).WithStateRoot(root).TestObject;
                using (ws.BeginScope(parent))
                {
                    (Block processed, TxReceipt[] receipts) = replay.ProcessOne(suggested, ProcessingOptions.NoValidation, NullBlockTracer.Instance, spec);
                    Assert.That(receipts.Length, Is.Zero);
                    Assert.That(ReferenceEquals(processed, suggested), Is.True);
                    ws.CommitTree(number); // ReplayScope.Commit verifies the recomputed root equals `root`.
                }

                Assert.That(provider.HasRoot(HeaderFor((number, root))), Is.True, $"state present at block {number}");
                parent = HeaderFor((number, root));
            }

            // The recorded path never falls through to EVM execution.
            inner.DidNotReceive().ProcessOne(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<IReleaseSpec>(), Arg.Any<CancellationToken>());
        }
    }

    private static BlockHeader HeaderFor((ulong Number, Hash256 Root) block)
        => Build.A.BlockHeader.WithNumber(block.Number).WithStateRoot(block.Root).TestObject;

    private static StateDiffStore MakeStore(string dir, bool recording = false, bool replay = false)
        => new(
            new StateDiffArchiveConfig { ArchivePath = dir, RecordingEnabled = recording, ReplayEnabled = replay },
            new InitConfig(),
            LimboLogs.Instance);
}
