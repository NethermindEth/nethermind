// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Paris : GrayGlacier
{
    private static IReleaseSpec _instance;

    protected Paris()
    {
        Name = "Paris";
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Paris());
}
