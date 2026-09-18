// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

/// <summary>The world-state scope is opened for the branch's target block and re-opened at the EIP-8347 activation.</summary>
public class BranchProcessorScopeTests
{
    private const ulong Activation = 48;
    private const int CommitPoint = 64;

    [TestCase(3, 2, false, "1,3", TestName = "activation inside the branch re-opens once")]
    [TestCase(3, -1, false, "1", TestName = "no activation keeps one scope for the branch")]
    [TestCase(CommitPoint + 2, CommitPoint + 1, false, "1,66", TestName = "activation right after the commit point re-opens once, not twice")]
    [TestCase(3, 2, true, "1,3", TestName = "a read-only branch re-opens too")]
    public void Scope_follows_the_activation(int blockCount, int activationIndex, bool readOnly, string expectedScopes)
    {
        List<string> scopes = [];
        IWorldState worldState = Substitute.For<IWorldState>();
        worldState.TryBeginScopeAtTarget(Arg.Any<BlockHeader>(), out Arg.Any<IDisposable>()).Returns(call =>
        {
            scopes.Add(call.ArgAt<BlockHeader>(0).Number.ToString());
            call[1] = Substitute.For<IDisposable>();
            return true;
        });
        IBlockProcessor blockProcessor = Substitute.For<IBlockProcessor>();
        blockProcessor.ProcessOne(Arg.Any<Block>(), Arg.Any<ProcessingOptions>(), Arg.Any<IBlockTracer>(), Arg.Any<IReleaseSpec>(), Arg.Any<CancellationToken>())
            .Returns(call => (call.Arg<Block>(), Array.Empty<TxReceipt>()));
        ISpecProvider specProvider = new CustomSpecProvider(
            ((ForkActivation)0, Prague.Instance),
            (ForkActivation.TimestampOnly(Activation), new OverridableReleaseSpec(Prague.Instance) { IsEip8347Enabled = true }));
        BranchProcessor processor = new(blockProcessor, specProvider, worldState, Substitute.For<IBlockhashProvider>(),
            Substitute.For<IInclusionListSatisfactionChecker>(), LimboLogs.Instance);

        Block parent = Build.A.Block.Genesis.WithTimestamp(0).TestObject;
        Block[] blocks = new Block[blockCount];
        for (int index = 0; index < blockCount; index++)
        {
            ulong timestamp = activationIndex >= 0 && index >= activationIndex ? Activation + (ulong)index : (ulong)(index / 4 + 1);
            blocks[index] = Build.A.Block.WithParent(index == 0 ? parent : blocks[index - 1]).WithTimestamp(timestamp).TestObject;
        }
        ProcessingOptions options = ProcessingOptions.NoValidation | (readOnly ? ProcessingOptions.ReadOnlyChain : ProcessingOptions.None);

        processor.Process(parent.Header, blocks, options, NullBlockTracer.Instance);

        Assert.That(string.Join(',', scopes), Is.EqualTo(expectedScopes));
    }
}
