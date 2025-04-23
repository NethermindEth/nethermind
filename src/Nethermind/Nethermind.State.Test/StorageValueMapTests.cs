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
    private static readonly StorageValue V3 = new(99494L);
    private static readonly StorageValue V4 = new(983409589304L);

    [Test]
    public void Maps_values()
    {
        using var map = new StorageValueMap(16);

        var ptr1 = map.Map(V1);
        var ptr2 = map.Map(V2);

        ptr1.Ref.Should().Be(V1);
        ptr2.Ref.Should().Be(V2);
    }

    [Test]
    public void Clear_should_clear_all_values()
    {
        // A tiny map to have collisions
        using var map = new StorageValueMap(2);

        var ptr1 = map.Map(V1);
        var ptr2 = map.Map(V2);

        ptr1.IsZero.Should().BeFalse();
        ptr2.IsZero.Should().BeFalse();

        map.Map(V3).IsZero.Should().BeTrue("No more space in the map");

        map.Clear();

        map.Map(V3).Ref.Should().Be(V3);
        map.Map(V4).Ref.Should().Be(V4);
    }
}
