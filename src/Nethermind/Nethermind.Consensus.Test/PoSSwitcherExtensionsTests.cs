// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class PoSSwitcherExtensionsTests
{
    [TestCase(7UL, false, 5, true, true, true, TestName = "Unflagged nonzero difficulty takes the switcher's post-merge")]
    [TestCase(7UL, false, 5, false, false, true, TestName = "Unflagged nonzero difficulty stays pre-merge when the switcher says so")]
    [TestCase(7UL, false, 0, false, true, false, TestName = "Zero difficulty is post-merge without asking the switcher")]
    [TestCase(7UL, true, 5, false, true, false, TestName = "A set flag is kept without asking the switcher")]
    [TestCase(0UL, true, 1, true, false, false, TestName = "A genesis with a nonzero difficulty is pre-merge whatever its flag and the switcher")]
    [TestCase(0UL, false, 0, false, true, false, TestName = "A genesis with a zero difficulty is post-merge without asking the switcher")]
    public void RestorePostMergeForCall_keeps_the_flag_or_zero_difficulty_and_otherwise_asks_the_switcher(
        ulong number, bool isPostMerge, int difficulty, bool switcherSays, bool expected, bool switcherAsked)
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.IsPostMerge(Arg.Any<BlockHeader>()).Returns(switcherSays);
        BlockHeader header = Build.A.BlockHeader.WithNumber(number).WithDifficulty((UInt256)difficulty).TestObject;
        header.IsPostMerge = isPostMerge;

        poSSwitcher.RestorePostMergeForCall(header);

        Assert.That(header.IsPostMerge, Is.EqualTo(expected));
        poSSwitcher.Received(switcherAsked ? 1 : 0).IsPostMerge(header);
    }
}
