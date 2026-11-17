// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test;

public class AuRaStateSyncPivotBlockTreeTests
{
    private const ulong SyncPivotNumber = 3;
    private static readonly Address[] Validators = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC];

    private IBlockTree _blockTree = null!;
    private IStateSyncPivot _inner = null!;
    private Block _head = null!;
    private ISpecProvider _specProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _blockTree = Build.A.BlockTree().OfChainLength(1).TestObject;
        _head = _blockTree.FindBlock(0, BlockTreeLookupOptions.None)!;
        _inner = Substitute.For<IStateSyncPivot>();
        _inner.CanFinalize(Arg.Any<BlockHeader>()).Returns(true);
        _specProvider = Substitute.For<ISpecProvider>();
        _inner.GetPivotHeader().Returns(_ => _blockTree.FindHeader(_head.Hash!, BlockTreeLookupOptions.None, _head.Number));
        Extend(SyncPivotNumber, static n => Validators[n % 3]);
        _blockTree.SyncPivot = (SyncPivotNumber, _blockTree.FindHeader(SyncPivotNumber, BlockTreeLookupOptions.None)!.Hash!);
    }

    private Block Extend(ulong count, Func<int, Address> signer, bool zeroDifficulty = false)
    {
        Block parent = _head;
        for (ulong i = 0; i < count; i++)
        {
            int number = (int)parent.Number + 1;
            BlockBuilder builder = Build.A.Block.WithParent(parent).WithBeneficiary(signer(number));
            if (zeroDifficulty) builder = builder.WithDifficulty(0);
            Block block = builder.TestObject;
            _blockTree.SuggestBlock(block, BlockTreeSuggestOptions.None);
            parent = block;
        }

        _blockTree.TryUpdateMainChain(parent.Header, wereProcessed: false, forceUpdateHeadBlock: true, parent);
        _head = parent;
        return parent;
    }

    [Test]
    public void Pre_merge_sync_uses_the_trusted_anchor_when_the_validator_set_is_unknown([Values] bool hasTerminalTotalDifficulty)
    {
        BlockHeader anchor = _head.Header;
        Extend(20, static number => Validators[number % Validators.Length]);
        if (hasTerminalTotalDifficulty) _specProvider.TerminalTotalDifficulty.Returns(UInt256.MaxValue);
        _blockTree.ForkChoiceUpdated(anchor.Hash, anchor.Hash);
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        AuRaStateSyncPivot pivot = new(_inner, _blockTree, _specProvider, LimboLogs.Instance, exitSource);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pivot.GetPivotHeader()?.Hash, Is.EqualTo(anchor.Hash));
            Assert.That(pivot.CanFinalize(anchor), Is.True);
            Assert.That(pivot.CanFinalize(_head.Header), Is.False);
        }

        exitSource.DidNotReceiveWithAnyArgs().Exit(default);
    }

    [Test]
    public void Reads_the_effective_terminal_total_difficulty_each_time()
    {
        Block postMergeAnchor = Extend(1, static _ => TestItem.AddressD, zeroDifficulty: true);
        _blockTree.SyncPivot = (postMergeAnchor.Number, postMergeAnchor.Hash!);
        Extend(5, static _ => TestItem.AddressD, zeroDifficulty: true);
        AuRaStateSyncPivot pivot = new(_inner, _blockTree, _specProvider, LimboLogs.Instance);

        Assert.That(pivot.GetPivotHeader()?.Hash, Is.EqualTo(postMergeAnchor.Hash));

        _specProvider.TerminalTotalDifficulty.Returns(UInt256.MaxValue);

        Assert.That(pivot.GetPivotHeader()?.Hash, Is.EqualTo(_head.Hash));
    }

    [Test]
    public void Stops_once_when_fork_choice_finalizes_a_post_merge_block_and_the_anchor_is_pre_merge()
    {
        _specProvider.TerminalTotalDifficulty.Returns(UInt256.MaxValue);
        Block postMerge = Extend(1, static _ => TestItem.AddressD, zeroDifficulty: true);
        _blockTree.ForkChoiceUpdated(postMerge.Hash!, postMerge.Hash!);
        IProcessExitSource exitSource = Substitute.For<IProcessExitSource>();
        AuRaStateSyncPivot pivot = new(_inner, _blockTree, _specProvider, LimboLogs.Instance, exitSource);

        Assert.That(pivot.GetPivotHeader(), Is.Null);
        Assert.That(pivot.GetPivotHeader(), Is.Null);
        exitSource.Received(1).Exit(ExitCodes.ConflictingConfigurations);
    }
}
