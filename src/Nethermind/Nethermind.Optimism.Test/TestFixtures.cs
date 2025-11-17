// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public static class TestFixtures
{
    public static IEnumerable<TestFixtureData> Forks => Spec.ForkNameAt.Select(p => p.Key == Spec.GenesisTimestamp
        ? new TestFixtureData(p.Key + 1) { TestName = "Genesis + 1" }
        : new TestFixtureData(p.Key) { TestName = p.Value }
    );
}
