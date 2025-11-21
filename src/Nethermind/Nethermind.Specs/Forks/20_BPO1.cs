// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class BPO1 : Osaka
{
    private static IReleaseSpec _instance;

    public BPO1()
    {
        Name = "bpo1";
        MaxBlobCount = 15;
        TargetBlobCount = 10;
        BlobBaseFeeUpdateFraction = 8346193;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new BPO1());
}
