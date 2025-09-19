// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class BPO2 : BPO1
{
    private static IReleaseSpec _instance;

    public BPO2()
    {
        Name = "bpo2";
        MaxBlobCount = 21;
        TargetBlobCount = 14;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new BPO2());
}
