// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The trie nodes a forked <see cref="Fork.SnapshotBundle"/> keeps for the <c>IsPersisted</c> replay: none for a
/// placeholder, nothing allocated until a node is set, and the persisted children of the nodes nobody took pruned when
/// the bundle goes, as returning a C# content prunes them.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class CaptureTests
{
    private static readonly FieldInfo Captures = typeof(Fork.SnapshotBundle).GetField("_captures", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Test]
    public async Task Disposing_the_bundle_prunes_the_nodes_of_its_commits_kept_by_none_and_of_its_current_content([Values] bool committedWithoutKeeping)
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality());
        (TrieNode branch, TrieNode persistedChild) = BranchOverPersistedChild();
        Fork.SnapshotBundle bundle = container.Resolve<Fork.IFlatDbManager>().GatherSnapshotBundle(StateId.PreGenesis, ResourcePool.Usage.MainBlockProcessing);
        bundle.SetStateNode(TreePath.Empty, branch);
        if (committedWithoutKeeping) bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(0, Keccak.Compute("0")), returnSnapshot: false);
        bool childHeldBefore = HoldsChild(branch, persistedChild);

        bundle.Dispose();

        Assert.That((childHeldBefore, HoldsChild(branch, persistedChild)), Is.EqualTo((true, false)));
    }

    [Test]
    public async Task Placeholders_are_not_kept_and_a_bundle_setting_no_node_allocates_no_capture()
    {
        RustNativeLibrary.Require();
        using TempPath path = TempPath.GetTempDirectory();
        await using FlatManagerContainer container = new(FlatManagerContainer.Config(FlatDbBackend.Rust), path.Path, new TestFinality());
        using Fork.SnapshotBundle bundle = container.Resolve<Fork.IFlatDbManager>().GatherSnapshotBundle(StateId.PreGenesis, ResourcePool.Usage.MainBlockProcessing);
        bundle.SetAccount(new Address("0xeeeeeeee00000000000000000000000000000001"), new Account(1, 1));
        bool allocatedBeforeANode = Captures.GetValue(bundle) is not null;
        bundle.SetStateNode(TreePath.Empty, new TrieNode(NodeType.Unknown, Keccak.Compute("placeholder")));
        bundle.SetStorageNode(Keccak.Compute("owner"), TreePath.Empty, new TrieNode(NodeType.Unknown, Keccak.Compute("storage placeholder")));

        (Fork.Snapshot? snapshot, TransientResource? transient) = bundle.CollectAndApplySnapshot(StateId.PreGenesis, new StateId(0, Keccak.Compute("0")));
        Fork.CapturedNodes? nodes = snapshot!.TakeNodes();
        snapshot.Dispose();
        TestInternals.ReleaseLease(transient!);

        Assert.That((allocatedBeforeANode, nodes), Is.EqualTo((false, (Fork.CapturedNodes?)null)));
    }

    /// <summary>A branch node whose one child, a leaf, is persisted; pruning the branch drops its reference to the child.</summary>
    private static (TrieNode Branch, TrieNode Child) BranchOverPersistedChild()
    {
        TrieNode child = new(NodeType.Leaf, Keccak.Compute("child"), Bytes.FromHexString("0xc28080")) { IsPersisted = true };
        TrieNode branch = new(NodeType.Branch);
        branch.SetChild(0, child);
        branch.SetChild(1, new TrieNode(NodeType.Leaf, Keccak.Compute("sibling"), Bytes.FromHexString("0xc28181")));
        TreePath root = TreePath.Empty;
        branch.ResolveKey(NullTrieNodeResolver.Instance, ref root);
        branch.Seal();
        return (branch, child);
    }

    private static bool HoldsChild(TrieNode branch, TrieNode child)
    {
        TreePath root = TreePath.Empty;
        return ReferenceEquals(branch.GetChild(NullTrieNodeResolver.Instance, ref root, 0), child);
    }
}
