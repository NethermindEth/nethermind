// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.Persistence;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class MigrationStateBoundaryTests
{
    private const ulong Activation = 48;

    private static ISpecProvider Specs() => new CustomSpecProvider(
        ((ForkActivation)0, Prague.Instance),
        (ForkActivation.TimestampOnly(Activation), new OverridableReleaseSpec(Prague.Instance) { IsEip8347Enabled = true }));

    [TestCase(false, false, 7UL, TestName = "nothing persisted in PBT: flat's pointer")]
    [TestCase(true, false, 7UL, TestName = "PBT keyed by an MPT root (pre-activation): flat's pointer")]
    [TestCase(true, true, 9UL, TestName = "PBT keyed by its own root (post-activation): PBT's pointer")]
    public void Boundary_reports_the_backend_that_can_be_redriven(bool pbtPersisted, bool pbtLive, ulong expected)
    {
        IPersistence flatPersistence = Substitute.For<IPersistence>();
        IPersistence.IPersistenceReader flatReader = Substitute.For<IPersistence.IPersistenceReader>();
        flatReader.CurrentState.Returns(new Flat.StateId(7, TestItem.KeccakA.ValueHash256));
        flatPersistence.CreateReader(Arg.Any<ReaderFlags>()).Returns(flatReader);
        IPbtPersistence pbtPersistence = Substitute.For<IPbtPersistence>();
        IPbtPersistence.IReader pbtReader = Substitute.For<IPbtPersistence.IReader>();
        pbtReader.CurrentState.Returns(pbtPersisted ? new StateId(9, TestItem.KeccakB.ValueHash256) : StateId.PreGenesis);
        pbtReader.CurrentRoot.Returns(pbtLive ? TestItem.KeccakB.ValueHash256 : TestItem.KeccakC.ValueHash256);
        pbtPersistence.CreateReader().Returns(pbtReader);

        MigrationStateBoundary boundary = new(new FlatStateBoundary(flatPersistence), pbtPersistence);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(boundary.BestPersistedState, Is.EqualTo(expected));
            Assert.That(boundary.OldestStateBlock, Is.EqualTo(expected));
            Assert.That(boundary.FindBestFullState(), Is.EqualTo(expected));
            Assert.That(boundary.RetentionWindowBlocks, Is.Null);
        }
    }

    [TestCase(2UL, 2UL, TestName = "finality below activation passes through")]
    [TestCase(5UL, 3UL, TestName = "finality past activation clamps to the activation parent")]
    public void Flat_finality_never_advances_past_the_activation_parent(ulong finalized, ulong expected)
    {
        BlockHeader[] chain = new BlockHeader[6];
        chain[0] = Build.A.BlockHeader.WithNumber(0).WithTimestamp(0).TestObject;
        for (int number = 1; number < chain.Length; number++)
            chain[number] = Build.A.BlockHeader.WithParent(chain[number - 1]).WithTimestamp(number >= 4 ? Activation + (ulong)number : (ulong)number * 12).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        foreach (BlockHeader header in chain)
        {
            blockTree.FindHeader(header.Number, BlockTreeLookupOptions.RequireCanonical).Returns(header);
            blockTree.FindHeader(header.Hash!, BlockTreeLookupOptions.RequireCanonical).Returns(header);
        }
        IFinalizedStateProvider inner = Substitute.For<IFinalizedStateProvider>();
        inner.FinalizedBlockNumber.Returns(finalized);
        inner.GetFinalizedStateRootAt(Arg.Any<ulong>()).Returns(call => chain[call.Arg<ulong>()].StateRoot);

        MigrationFlatFinalizedStateProvider provider = new(inner, blockTree, Specs());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.FinalizedBlockNumber, Is.EqualTo(expected));
            Assert.That(provider.GetFinalizedStateRootAt(3), Is.EqualTo(chain[3].StateRoot));
            Assert.That(provider.GetFinalizedStateRootAt(4), Is.Null, "post-activation boundaries carry PBT roots flat never holds");
        }
    }
}
