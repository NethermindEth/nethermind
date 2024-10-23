// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class TotalDifficultyStrategyTests
{
    [Test]
    public void CumulativeTotalDifficultyStrategy_own_TotalDifficulty_is_Parent_TotalDifficulty_plus_own_difficulty()
    {
        ITotalDifficultyStrategy strategy = new CumulativeTotalDifficultyStrategy();

        List<(BlockHeader Header, UInt256 ExpectedTotalDifficulty)> headers =
        [
            (Build.A.BlockHeader.WithDifficulty(10).WithTotalDifficulty(39).TestObject, 39),
            (Build.A.BlockHeader.WithDifficulty(8).TestObject, 29),
            (Build.A.BlockHeader.WithDifficulty(5).TestObject, 21),
            (Build.A.BlockHeader.WithDifficulty(12).TestObject, 16),
            (Build.A.BlockHeader.WithDifficulty(3).TestObject, 4),
            (Build.A.BlockHeader.WithDifficulty(1).TestObject, 1),
        ];

        for (int i = 0; i < headers.Count - 1; i++)
        {
            var header = headers[i].Header;
            var parent = headers[i + 1].Header;

            parent.TotalDifficulty = strategy.ParentTotalDifficulty(header);
        }

        foreach (var (header, expectedTotalDifficulty) in headers)
        {
            header.TotalDifficulty.Should().Be(expectedTotalDifficulty);
        }
    }
}
