// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class TinyTreePathTests
{
    [Test]
    public void Should_ConvertFromAndToTreePath()
    {
        TreePath path = new TreePath(new ValueHash256("0123456789abcd00000000000000000000000000000000000000000000000000"), 14);

        TinyTreePath tinyPath = new TinyTreePath(path);

        tinyPath.ToTreePath().Should().Be(path);
    }

    [Test]
    public void When_PathIsTooLong_Should_Throw()
    {
        TreePath path = new TreePath(new ValueHash256("0123456789000000000000000000000000000000000000000000000000000000"), 15);

        Action act = () => new TinyTreePath(path);
        act.Should().Throw<InvalidOperationException>();
    }
}

