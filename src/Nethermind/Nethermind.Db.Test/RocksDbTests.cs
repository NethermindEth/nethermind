// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Db.Rocks;
using NUnit.Framework;

namespace Nethermind.Db.Test;

[Parallelizable(ParallelScope.All)]
internal static class RocksDbTests
{
    [Test]
    public static void Should_have_required_version() => DbOnTheRocks.GetRocksDbVersion().Should().Be("8.3.2");
}
