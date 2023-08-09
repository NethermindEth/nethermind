// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Core.Test.RequestSizer;

public class AdaptiveRequestSizerTests
{
    [TestCase(1, 100, 20, AdaptiveRequestSizer.Direction.Increase, 40)]
    [TestCase(1, 100, 20, AdaptiveRequestSizer.Direction.Decrease, 10)]
    [TestCase(1, 100, 20, AdaptiveRequestSizer.Direction.Stay, 20)]
    [TestCase(1, 100, 1, AdaptiveRequestSizer.Direction.Increase, 2)]
    [TestCase(1, 100, 1, AdaptiveRequestSizer.Direction.Decrease, 1)]
    [TestCase(1, 100, 1, AdaptiveRequestSizer.Direction.Stay, 1)]
    [TestCase(1, 100, 100, AdaptiveRequestSizer.Direction.Increase, 100)]
    [TestCase(1, 100, 100, AdaptiveRequestSizer.Direction.Decrease, 50)]
    [TestCase(1, 100, 100, AdaptiveRequestSizer.Direction.Stay, 100)]
    public async Task Test_Threshold(int minSize, int maxSize, int startingSize, AdaptiveRequestSizer.Direction direction, int afterRequestSize)
    {
        AdaptiveRequestSizer sizer = new(minSize, maxSize) { RequestSize = startingSize };

        await sizer.Run((async requestSize => (requestSize, direction)));

        sizer.RequestSize.Should().Be(afterRequestSize);
    }
}
