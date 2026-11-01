// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

/// <summary>
/// Range proofs whose start or end runs through an extension node. The state trie, and the storage trie of the account
/// at <see cref="KeyD"/>, hold the keys A to D:
/// <code>
/// R = branch { 3: leaf A, b: E, f: leaf D }
/// E = extension [1] -> branch { 0: leaf B, 1: leaf C }
/// </code>
/// The storage trie of the account at <see cref="KeyA"/> holds two slots that share their first two nibbles, so its
/// root is an extension.
/// </summary>
[TestFixture]
public class SnapRangeProofBoundaryTests
{
    private static readonly ValueHash256 KeyA = new("0x3000000000000000000000000000000000000000000000000000000000000001");
    private static readonly ValueHash256 KeyB = new("0xb10aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly ValueHash256 KeyC = new("0xb11aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly ValueHash256 KeyD = new("0xf000000000000000000000000000000000000000000000000000000000000001");
    private static readonly ValueHash256[] Keys = [KeyA, KeyB, KeyC, KeyD];

    // Just before leaf D: the proof of this key ends at leaf D, which a range ending here keeps as a hash.
    private static readonly ValueHash256 KeyBeforeD = new("0xf000000000000000000000000000000000000000000000000000000000000000");

    private static readonly ValueHash256 RootExtensionSlot1 = new("0xab1ccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
    private static readonly ValueHash256 RootExtensionSlot2 = new("0xab2ccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");

    public enum Backend { Patricia, Flat }

    [Test]
    public void AddAccountRange_OriginUnderAnExtensionWhoseChildIsNotInTheProof_IsAnInvalidProof()
    {
        Source source = new();

        // The keys B and C, at and after the origin, are left out together with the extension's child.
        (AddRangeResult result, _, _, _, _) = SnapProviderHelper.AddAccountRange(
            CreatePatriciaFactory(), 0, source.StateRoot, KeyB, ValueKeccak.MaxValue,
            [source.AccountEntry(KeyD)], Proofs(WithoutExtensionChild(source.AccountProof(KeyB))));

        Assert.That(result, Is.EqualTo(AddRangeResult.InvalidProof));
    }

    [Test]
    public void AddAccountRange_OriginUnderAnExtension_IsAcceptedFromTheSnapServer()
    {
        Source source = new();
        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
            source.Server.GetAccountRanges(source.StateRoot, KeyB, ValueKeccak.MaxValue, 1.MB, CancellationToken.None);
        using (accounts)
        using (proofs)
        {
            Assert.That(accounts.Select(static a => a.Path), Is.EqualTo(new[] { KeyB, KeyC, KeyD }), "guards the premise");

            (AddRangeResult result, _, _, _, _) = SnapProviderHelper.AddAccountRange(
                CreatePatriciaFactory(), 0, source.StateRoot, KeyB, ValueKeccak.MaxValue, accounts, proofs);

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }
    }

    // The origin passes the extension by: the proof of the origin ends at the extension, whose keys all lie before the
    // range, so the extension rightly keeps its child as a hash.
    [Test]
    public void AddAccountRange_ExtensionWhollyBeforeTheOrigin_IsAcceptedFromTheSnapServerWithoutItsChild()
    {
        Source source = new();
        ValueHash256 origin = new("0xb200000000000000000000000000000000000000000000000000000000000000");
        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
            source.Server.GetAccountRanges(source.StateRoot, origin, ValueKeccak.MaxValue, 1.MB, CancellationToken.None);
        using (accounts)
        using (proofs)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(accounts.Select(static a => a.Path), Is.EqualTo(new[] { KeyD }), "guards the premise");
                Assert.That(ContainsExtensionChild(proofs), Is.False, "guards the premise");
            }

            (AddRangeResult result, _, _, _, _) = SnapProviderHelper.AddAccountRange(
                CreatePatriciaFactory(), 0, source.StateRoot, origin, ValueKeccak.MaxValue, accounts, proofs);

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }
    }

    [TestCase(Backend.Patricia)]
    [TestCase(Backend.Flat)]
    public void AddAccountRange_LastEntryUnderAnExtensionWhoseChildIsNotInTheProof_IsAnInvalidProof(Backend backend)
    {
        Source source = new();
        using FactoryScope scope = new(backend);

        (AddRangeResult result, _, _, _, _) = SnapProviderHelper.AddAccountRange(
            scope.Factory, 0, source.StateRoot, ValueKeccak.Zero, ValueKeccak.MaxValue,
            [source.AccountEntry(KeyA), source.AccountEntry(KeyB)],
            Proofs([.. source.AccountProof(ValueKeccak.Zero), .. WithoutExtensionChild(source.AccountProof(KeyB))]));

        Assert.That(result, Is.EqualTo(AddRangeResult.InvalidProof));
    }

    [Test]
    public void AddStorageRange_ContinuationUnderAnExtensionWhoseChildIsNotInTheProof_IsAnInvalidProof()
    {
        Source source = new();

        (AddRangeResult result, _, _, _) = SnapProviderHelper.AddStorageRange(
            CreatePatriciaFactory(), source.AccountD, [source.SlotEntry(KeyD)], KeyB, null,
            Proofs(WithoutExtensionChild(source.StorageProof(KeyD, KeyB))));

        Assert.That(result, Is.EqualTo(AddRangeResult.InvalidProof));
    }

    [Test]
    public void AddStorageRange_ContinuationUnderAnExtension_IsAcceptedFromTheSnapServer()
    {
        Source source = new();
        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList? proofs) = source.Server.GetStorageRanges(
            source.StateRoot, [source.AccountD], KeyB, ValueKeccak.MaxValue, 1.MB, CancellationToken.None);
        try
        {
            Assert.That(slots[0].Select(static s => s.Path), Is.EqualTo(new[] { KeyB, KeyC, KeyD }), "guards the premise");

            (AddRangeResult result, _, _, _) = SnapProviderHelper.AddStorageRange(
                CreatePatriciaFactory(), source.AccountD, slots[0], KeyB, null, proofs);

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }
        finally
        {
            slots.DisposeRecursive();
            proofs?.Dispose();
        }
    }

    // A proof of absence for [0x40.., 0xb0ff..] built from the proofs of both ends: the extension lies wholly after the
    // range, so it keeps its child as a hash.
    [Test]
    public void AddStorageRange_ProofOfAbsenceBeforeAnExtension_IsAcceptedWithoutItsChild()
    {
        Source source = new();
        ValueHash256 origin = new("0x4000000000000000000000000000000000000000000000000000000000000000");
        ValueHash256 limit = new("0xb0ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
        byte[][] proof = [.. source.StorageProof(KeyD, origin), .. source.StorageProof(KeyD, limit)];
        Assert.That(ContainsExtensionChild(Proofs(proof)), Is.False, "guards the premise");

        (AddRangeResult result, bool moreChildrenToRight, _, _) = SnapProviderHelper.AddStorageRange(
            CreatePatriciaFactory(), source.AccountD, [], origin, limit, Proofs(proof));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
            Assert.That(moreChildrenToRight, Is.False);
        }
    }

    // The extension is the storage root: on its own it "proves" that no slot follows the origin.
    [Test]
    public void AddStorageRange_ProofOfAbsenceOfOnlyTheRootExtension_IsAnInvalidProofAndTheAccountIsReprovenFromItsOrigin()
    {
        Source source = new();
        ValueHash256 origin = RootExtensionSlot1;
        byte[][] proof = WithoutExtensionChild(source.StorageProof(KeyA, origin));
        Assert.That(proof, Has.Length.EqualTo(1), "guards the premise: the proof is the root extension alone");

        using IContainer container = CreateContainer(source.StateRoot);
        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(progressTracker);

        Assert.That(AnswerStorageContinuationWithProofOnly(snapProvider, progressTracker, source.AccountA, origin, proof),
            Is.EqualTo(AddRangeResult.InvalidProof));

        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        {
            Assert.That(refresh?.AccountsToRefreshRequest?.Paths[0].StorageStartingHash, Is.EqualTo(origin),
                "the account is re-proven from its origin, not completed");
        }
    }

    [Test]
    public void AddStorageRange_ProofOfAbsenceUnderTheRootExtension_IsAcceptedFromTheSnapServer()
    {
        Source source = new();
        ValueHash256 origin = RootExtensionSlot2.IncrementPath();
        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList? proofs) = source.Server.GetStorageRanges(
            source.StateRoot, [source.AccountA], origin, ValueKeccak.MaxValue, 1.MB, CancellationToken.None);
        byte[][] proof;
        try
        {
            Assert.That(slots, Is.Empty, "guards the premise: the server answers with a proof of absence");
            IByteArrayList serverProofs = proofs!;
            proof = [.. Enumerable.Range(0, serverProofs.Count).Select(i => serverProofs[i].ToArray())];
        }
        finally
        {
            slots.DisposeRecursive();
            proofs?.Dispose();
        }

        using IContainer container = CreateContainer(source.StateRoot);
        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(progressTracker);

        AddRangeResult result = AnswerStorageContinuationWithProofOnly(snapProvider, progressTracker, source.AccountA, origin, proof);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
            Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.True, "nothing is queued again for the account");
        }
    }

    // Slots made up under the root extension, with only the extension as proof. The extension lies inside the range, so
    // its missing child is caught before the bulk set.
    [TestCase(Backend.Patricia)]
    [TestCase(Backend.Flat)]
    public void AddStorageRange_SlotsUnderAnExtensionWhoseChildIsNotInTheProof_AreAnInvalidProof(Backend backend)
    {
        Source source = new();
        using FactoryScope scope = new(backend);

        List<PathWithStorageSlot> slots = [];
        for (int nibble = 0; nibble < 16; nibble++)
        {
            for (int i = 0; i < (nibble == 0xa ? 1 : 9); i++)
            {
                byte[] key = Keccak.Compute(new byte[] { (byte)nibble, (byte)i }).BytesToArray();
                key[0] = nibble == 0xa ? (byte)0xab : (byte)((nibble << 4) | (key[0] & 0x0f));
                if (nibble == 0xa) key[1] = (byte)(0x30 | (key[1] & 0x0f));
                slots.Add(new PathWithStorageSlot(new ValueHash256(key), Rlp.Encode(new byte[] { 1 }).Bytes));
            }
        }
        slots.Sort(static (x, y) => x.Path.CompareTo(y.Path));

        (AddRangeResult result, _, _, _) = SnapProviderHelper.AddStorageRange(
            scope.Factory, source.AccountA, slots, null, null, Proofs(WithoutExtensionChild(source.StorageProof(KeyA, RootExtensionSlot1))));

        Assert.That(result, Is.EqualTo(AddRangeResult.InvalidProof));
    }

    // A made-up account just before leaf D: adding it needs leaf D, which the proof leaves out.
    [TestCase(Backend.Patricia)]
    [TestCase(Backend.Flat)]
    public void AddAccountRange_EntryReachingALeafTheProofKeepsAsAHash_IsAnInvalidProof(Backend backend)
    {
        Source source = new();
        using FactoryScope scope = new(backend);

        (AddRangeResult result, _, _, _, _) = SnapProviderHelper.AddAccountRange(
            scope.Factory, 0, source.StateRoot, ValueKeccak.Zero, ValueKeccak.MaxValue,
            [MadeUpAccountBeforeD()], Proofs(source.AccountProof(KeyBeforeD)));

        Assert.That(result, Is.EqualTo(AddRangeResult.InvalidProof));
    }

    // Made-up accounts over every first nibble, enough for the parallel bulk set, the one under f reaching leaf D, which
    // the proof keeps as a hash. No extension lies on this proof, so only the catch around the bulk set rejects the range.
    [TestCase(Backend.Patricia)]
    [TestCase(Backend.Flat)]
    public void AddAccountRange_ManyEntriesOneReachingALeafTheProofKeepsAsAHash_IsAnInvalidProof(Backend backend)
    {
        Source source = new();
        using FactoryScope scope = new(backend);

        List<PathWithAccount> accounts = [MadeUpAccountBeforeD()];
        for (int nibble = 0; nibble < 0xf; nibble++)
        {
            for (int i = 0; i < 9; i++)
            {
                byte[] key = Keccak.Compute(new byte[] { (byte)nibble, (byte)i }).BytesToArray();
                key[0] = (byte)((nibble << 4) | (key[0] & 0x0f));
                accounts.Add(new PathWithAccount(new ValueHash256(key), Build.An.Account.WithBalance(9).TestObject));
            }
        }
        accounts.Sort(static (x, y) => x.Path.CompareTo(y.Path));
        Assert.That(accounts, Has.Count.GreaterThanOrEqualTo(PatriciaTree.MinEntriesToParallelizeThreshold), "guards the premise");

        (AddRangeResult result, _, _, _, _) = SnapProviderHelper.AddAccountRange(
            scope.Factory, 0, source.StateRoot, ValueKeccak.Zero, ValueKeccak.MaxValue,
            accounts, Proofs(source.AccountProof(KeyBeforeD)));

        Assert.That(result, Is.EqualTo(AddRangeResult.InvalidProof));
    }

    // Only the missing-node exceptions show that the entries do not fit the proof. Any other failure of the bulk set,
    // such as a local store error, is not the peer's doing and is thrown as it is.
    [TestCaseSource(nameof(OtherBulkSetFailures))]
    public void VerifyAccountRange_BulkSetFailingForAnotherReason_ThrowsThatFailure(Func<Exception> createFailure)
    {
        Exception failure = createFailure();
        ISnapTree<PathWithAccount> tree = Substitute.For<ISnapTree<PathWithAccount>>();
        tree.When(static t => t.BulkSetAndUpdateRootHash(Arg.Any<IReadOnlyList<PathWithAccount>>())).Do(_ => throw failure);
        ISnapTrieFactory factory = Substitute.For<ISnapTrieFactory>();
        factory.CreateStateTree().Returns(tree);

        Assert.That(() => SnapProviderHelper.VerifyAccountRange(
                factory, TestItem.KeccakA, ValueKeccak.Zero, ValueKeccak.MaxValue, [MadeUpAccountBeforeD()], null),
            Throws.Exception.SameAs(failure));
    }

    private static IEnumerable<TestCaseData> OtherBulkSetFailures()
    {
        yield return new TestCaseData((Func<Exception>)(static () => new TrieException("A local failure.")))
            .SetName("{m}(TrieException)");
        yield return new TestCaseData((Func<Exception>)(static () => new InvalidOperationException("A local failure.")))
            .SetName("{m}(InvalidOperationException)");
    }

    // A proof of absence for [0x40.., 0xb1ff..]: the limit runs through the extension, whose keys B and C lie in the
    // range, and the start does not reach it.
    [Test]
    public void AddStorageRange_ProofOfAbsenceWhoseLimitRunsThroughAnExtensionWhoseChildIsNotInTheProof_IsAnInvalidProof()
    {
        Source source = new();
        ValueHash256 origin = new("0x4000000000000000000000000000000000000000000000000000000000000000");
        ValueHash256 limit = new("0xb1ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
        byte[][] proof = [.. source.StorageProof(KeyD, origin), .. WithoutExtensionChild(source.StorageProof(KeyD, limit))];

        (AddRangeResult result, _, _, _) = SnapProviderHelper.AddStorageRange(
            CreatePatriciaFactory(), source.AccountD, [], origin, limit, Proofs(proof));

        Assert.That(result, Is.EqualTo(AddRangeResult.InvalidProof));
    }

    // A proof of absence for [0x10.., max] made of the root extension alone, which lies inside the range with both slots.
    [Test]
    public void AddStorageRange_ProofOfAbsenceOfARootExtensionInsideTheRange_IsAnInvalidProof()
    {
        Source source = new();
        ValueHash256 origin = new("0x1000000000000000000000000000000000000000000000000000000000000000");
        byte[][] proof = source.StorageProof(KeyA, origin);
        Assert.That(proof, Has.Length.EqualTo(1), "guards the premise: the proof of the origin is the root extension alone");

        (AddRangeResult result, _, _, _) = SnapProviderHelper.AddStorageRange(
            CreatePatriciaFactory(), source.AccountA, [], origin, null, Proofs(proof));

        Assert.That(result, Is.EqualTo(AddRangeResult.InvalidProof));
    }

    [Test]
    public void HandleResponse_RangesWhoseEntriesDoNotFitTheProof_GetThePeerReported()
    {
        Source source = new();
        using IContainer container = CreateContainer(source.StateRoot);
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        Synchronization.SnapSync.SnapSyncFeed feed = (Synchronization.SnapSync.SnapSyncFeed)container.Resolve<ISimpleSyncFeed<SnapSyncBatch>>();
        PeerInfo peer = new(Substitute.For<ISyncPeer>());
        // A success from another peer, so the feed does not take the failures below for a stale pivot of its only peer.
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, new PeerInfo(Substitute.For<ISyncPeer>()));

        List<SyncResponseHandlingResult> results = [];
        for (int i = 0; i <= Synchronization.SnapSync.SnapSyncFeed.AllowedInvalidResponses; i++)
        {
            progressTracker.IsFinished(out SnapSyncBatch? batch);
            Assert.That(batch?.AccountRangeRequest?.StartingHash, Is.EqualTo(ValueKeccak.Zero), "the range is offered again");
            batch!.AccountRangeResponse = new AccountsAndProofs
            {
                PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { MadeUpAccountBeforeD() },
                Proofs = Proofs(source.AccountProof(KeyBeforeD))
            };

            // The feed disposes the batch.
            results.Add(feed.HandleResponse(batch, peer));
        }

        Assert.That(results, Is.EqualTo(Enumerable.Repeat(SyncResponseHandlingResult.OK, Synchronization.SnapSync.SnapSyncFeed.AllowedInvalidResponses)
            .Append(SyncResponseHandlingResult.LesserQuality)));
    }

    [Test]
    public void RefreshAccounts_AccountUnderAnExtensionWhoseChildIsNotInTheProof_IsRetriedRatherThanDropped()
    {
        Source source = new();
        using IContainer container = CreateContainer(source.StateRoot);
        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(progressTracker);

        PathWithAccount refreshed = new(KeyB, Build.An.Account.WithStorageRoot(TestItem.KeccakG).TestObject);
        progressTracker.EnqueueAccountRefresh(refreshed, ValueKeccak.Zero, Keccak.MaxValue);
        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        {
            Assert.That(refresh?.AccountsToRefreshRequest, Is.Not.Null);
            refresh!.AccountsToRefreshResponse = new AccountsAndProofs
            {
                PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { source.AccountEntry(KeyD) },
                Proofs = Proofs(WithoutExtensionChild(source.AccountProof(KeyB)))
            };

            Assert.That(snapProvider.RefreshAccounts(refresh.AccountsToRefreshRequest!, refresh.AccountsToRefreshResponse),
                Is.EqualTo(AddRangeResult.DifferentRootHash), "the reply does not prove that the account is gone");
            snapProvider.ReleaseRequest(refresh, responseHandled: true);
        }

        progressTracker.IsFinished(out SnapSyncBatch? retry);
        using (retry)
        {
            Assert.That(retry?.AccountsToRefreshRequest, Is.Not.Null, "the account is refreshed again");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retry!.AccountsToRefreshRequest!.Paths[0].PathAndAccount?.Path, Is.EqualTo(KeyB));
                Assert.That(refreshed.Account!.StorageRoot, Is.EqualTo(TestItem.KeccakG), "nothing was adopted");
            }
        }
    }

    [Test]
    public void RefreshAccounts_AccountUnderAnExtension_IsVerifiedFromTheSnapServer()
    {
        Source source = new();
        using IContainer container = CreateContainer(source.StateRoot);
        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(progressTracker);

        PathWithAccount refreshed = new(KeyB, Build.An.Account.WithStorageRoot(TestItem.KeccakG).TestObject);
        progressTracker.EnqueueAccountRefresh(refreshed, ValueKeccak.Zero, Keccak.MaxValue);
        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        {
            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                source.Server.GetAccountRanges(source.StateRoot, KeyB, KeyB.IncrementPath(), 4000, CancellationToken.None);
            refresh!.AccountsToRefreshResponse = new AccountsAndProofs { PathAndAccounts = accounts, Proofs = proofs };

            Assert.That(snapProvider.RefreshAccounts(refresh.AccountsToRefreshRequest!, refresh.AccountsToRefreshResponse), Is.EqualTo(AddRangeResult.OK));
            snapProvider.ReleaseRequest(refresh, responseHandled: true);
        }

        Assert.That(refreshed.Account!.StorageRoot, Is.EqualTo(source.Accounts[KeyB].StorageRoot), "the proven storage root is adopted");
    }

    private static PathWithAccount MadeUpAccountBeforeD() => new(KeyBeforeD, Build.An.Account.WithBalance(9).TestObject);

    private static ContainerBuilder CreateContainerBuilder(Hash256 stateRoot) =>
        new ContainerBuilder()
            .AddModule(new TestSynchronizerModule(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 }))
            .WithSuggestedHeaderOfStateRoot(stateRoot);

    private static IContainer CreateContainer(Hash256 stateRoot) => CreateContainerBuilder(stateRoot).Build();

    private static PatriciaSnapTrieFactory CreatePatriciaFactory() =>
        new(new NodeStorage(new MemDb()), new MemDb(), LimboLogs.Instance);

    private static IByteArrayList Proofs(byte[][] nodes) => new ByteArrayListAdapter(nodes.ToPooledList());

    private static TrieNode Decode(ReadOnlySpan<byte> rlp)
    {
        TrieNode node = new(NodeType.Unknown, rlp.ToArray());
        node.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        return node;
    }

    /// <summary>The nodes of a key's proof down to the first extension node, without the nodes below it.</summary>
    private static byte[][] WithoutExtensionChild(byte[][] proof)
    {
        List<byte[]> nodes = [];
        foreach (byte[] node in proof)
        {
            nodes.Add(node);
            if (Decode(node).IsExtension) break;
        }

        Assert.That(nodes, Has.Count.LessThan(proof.Length), "guards the premise: the proof runs through an extension");
        return [.. nodes];
    }

    private static bool ContainsExtensionChild(IByteArrayList proofs)
    {
        HashSet<ValueHash256> hashes = [];
        List<TrieNode> nodes = [];
        for (int i = 0; i < proofs.Count; i++)
        {
            hashes.Add(ValueKeccak.Compute(proofs[i]));
            nodes.Add(Decode(proofs[i]));
        }

        Assert.That(nodes.Any(static n => n.IsExtension), "guards the premise: the proof holds an extension");
        return nodes.Any(n => n.IsExtension && n.GetChildHashAsValueKeccak(1, out ValueHash256 child) && hashes.Contains(child));
    }

    private static void DrainAccountRangePartition(ProgressTracker progressTracker)
    {
        progressTracker.IsFinished(out SnapSyncBatch? batch);
        ValueHash256 partitionLimit = batch!.AccountRangeRequest!.LimitHash!.Value;
        progressTracker.UpdateAccountRangePartitionProgress(partitionLimit, Keccak.MaxValue, false);
        progressTracker.ReportAccountRangePartitionFinished(partitionLimit);
        batch.Dispose();
    }

    /// <summary>Queues a storage continuation from <paramref name="origin"/> and answers it with no slot list and only <paramref name="proof"/>.</summary>
    private static AddRangeResult AnswerStorageContinuationWithProofOnly(
        SnapProvider snapProvider, ProgressTracker progressTracker, PathWithAccount account, ValueHash256 origin, byte[][] proof)
    {
        progressTracker.EnqueueNextSlot(new StorageRange
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
            StartingHash = origin,
            LimitHash = Keccak.MaxValue
        });

        progressTracker.IsFinished(out SnapSyncBatch? batch);
        using (batch)
        {
            Assert.That(batch?.StorageRangeRequest, Is.Not.Null);
            batch!.StorageRangeResponse = new SlotsAndProofs
            {
                PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(0),
                Proofs = Proofs(proof),
            };

            AddRangeResult result = snapProvider.AddStorageRange(batch.StorageRangeRequest!, batch.StorageRangeResponse);
            snapProvider.ReleaseRequest(batch, responseHandled: true);
            return result;
        }
    }

    private sealed class FactoryScope : IDisposable
    {
        private readonly SnapshotableMemColumnsDb<FlatDbColumns>? _flatDb;

        public FactoryScope(Backend backend)
        {
            if (backend == Backend.Patricia)
            {
                Factory = CreatePatriciaFactory();
                return;
            }

            _flatDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
            Factory = new FlatSnapTrieFactory(
                new RocksDbPersistence(_flatDb, LimboLogs.Instance, NullTrieNodeLog.Instance),
                Substitute.For<IPersistenceManager>(), _flatDb, new MemDb(), new TestSyncConfig(), LimboLogs.Instance);
        }

        public ISnapTrieFactory Factory { get; }

        public void Dispose() => _flatDb?.Dispose();
    }

    /// <summary>The state described on the class, with an honest snap server over it.</summary>
    private sealed class Source
    {
        private readonly StateTree _state;

        public Source()
        {
            TestRawTrieStore store = new(new TestMemDb());

            StorageTree storageA = new(store.GetTrieStore(KeyA.ToCommitment()), LimboLogs.Instance);
            storageA.Set(RootExtensionSlot1, SlotValue(RootExtensionSlot1), false);
            storageA.Set(RootExtensionSlot2, SlotValue(RootExtensionSlot2), false);
            storageA.Commit();

            StorageTree storageD = new(store.GetTrieStore(KeyD.ToCommitment()), LimboLogs.Instance);
            foreach (ValueHash256 key in Keys) storageD.Set(key, SlotValue(key), false);
            storageD.Commit();

            for (int i = 0; i < Keys.Length; i++)
            {
                Account account = Build.An.Account.WithBalance((ulong)(i + 1)).TestObject;
                if (Keys[i] == KeyA) account = account.WithChangedStorageRoot(storageA.RootHash);
                if (Keys[i] == KeyD) account = account.WithChangedStorageRoot(storageD.RootHash);
                Accounts[Keys[i]] = account;
            }

            _state = new StateTree(store.GetTrieStore(null), LimboLogs.Instance);
            foreach ((ValueHash256 path, Account account) in Accounts) _state.Set(path.ToCommitment(), account);
            _state.Commit();

            Server = new SnapStateServer(store.AsReadOnly(), LimboLogs.Instance);
        }

        public Dictionary<ValueHash256, Account> Accounts { get; } = [];

        public Hash256 StateRoot => _state.RootHash;

        public SnapStateServer Server { get; }

        public PathWithAccount AccountA => AccountEntry(KeyA);

        public PathWithAccount AccountD => AccountEntry(KeyD);

        public PathWithAccount AccountEntry(in ValueHash256 path) => new(path, Accounts[path]);

        public PathWithStorageSlot SlotEntry(in ValueHash256 path) => new(path, SlotValue(path));

        /// <summary>The Merkle proof of <paramref name="path"/> in the state trie, from the root down.</summary>
        public byte[][] AccountProof(in ValueHash256 path)
        {
            AccountProofCollector collector = new(path.Bytes, Array.Empty<ValueHash256>());
            _state.Accept(collector, _state.RootHash);
            return collector.BuildResult().Proof;
        }

        /// <summary>The Merkle proof of <paramref name="slot"/> in the storage trie of <paramref name="account"/>, from its root down.</summary>
        public byte[][] StorageProof(in ValueHash256 account, in ValueHash256 slot)
        {
            AccountProofCollector collector = new(account.Bytes, new[] { slot });
            _state.Accept(collector, _state.RootHash);
            return collector.BuildResult().StorageProofs[0].Proof;
        }

        private static byte[] SlotValue(in ValueHash256 path) => Rlp.Encode(path.Bytes[..4]).Bytes;
    }
}
