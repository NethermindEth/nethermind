// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class StorageValueMapTests
{
    private static readonly StorageValue V1 = new(732847304UL);
    private static readonly StorageValue V2 = new(34859893849589308UL);

    [Test]
    public void Maps_values()
    {
        using var map = new StorageValueMap();

        var ptr1 = map.Map(V1);
        var ptr2 = map.Map(V2);

        ptr1.Ref.Should().Be(V1);
        ptr2.Ref.Should().Be(V2);
    }
}
