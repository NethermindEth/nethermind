// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Core.Test.RequestSizer;

public class AdaptiveRequestSizerTests
{
    [TestCase(1, 100, 20, AdaptiveRequestSizer.Direction.Increase, 30)]
    [TestCase(1, 100, 20, AdaptiveRequestSizer.Direction.Decrease, 13)]
    [TestCase(1, 100, 20, AdaptiveRequestSizer.Direction.Stay, 20)]
    [TestCase(1, 100, 1, AdaptiveRequestSizer.Direction.Increase, 2)]
    [TestCase(1, 100, 1, AdaptiveRequestSizer.Direction.Decrease, 1)]
    [TestCase(1, 100, 1, AdaptiveRequestSizer.Direction.Stay, 1)]
    [TestCase(1, 100, 100, AdaptiveRequestSizer.Direction.Increase, 100)]
    [TestCase(1, 100, 100, AdaptiveRequestSizer.Direction.Decrease, 66)]
    [TestCase(1, 100, 100, AdaptiveRequestSizer.Direction.Stay, 100)]
    public async Task Test_Threshold(int minSize, int maxSize, int startingSize, AdaptiveRequestSizer.Direction direction, int afterRequestSize)
    {
        AdaptiveRequestSizer sizer = new(minSize, maxSize, startingSize);

        await sizer.Run((requestSize => Task.FromResult((requestSize, direction))));

        sizer.RequestSize.Should().Be(afterRequestSize);
    }
}
