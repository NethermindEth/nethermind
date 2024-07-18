// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class ExitOnBlocknumberHandlerTests
{
    [TestCase(10, false)]
    [TestCase(99, false)]
    [TestCase(100, true)]
    [TestCase(101, true)]
    public void Will_Exit_When_BlockReached(long blockNumber, bool exitReceived)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        new ExitOnBlockNumberHandler(blockTree, processExitSource, 100, LimboLogs.Instance);

        blockTree.BlockAddedToMain += Raise.EventWith(
            new BlockReplacementEventArgs(Build.A.Block.WithNumber(blockNumber).TestObject));

        if (exitReceived)
        {
            processExitSource.Received().Exit(0);
        }
        else
        {
            processExitSource.DidNotReceive().Exit(0);
        }
    }
}
