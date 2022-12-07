// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Prague: Cancun
{
    private static IReleaseSpec _instance;

    protected Prague()
    {
        Name = "Prague";
        IsVerkleTreeEipEnabled = true;
    }

    public static new IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Prague());
}
