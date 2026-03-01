// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class BPO5 : BPO4
{
    private static IReleaseSpec _instance;

    public BPO5()
    {
        Name = "bpo5";
        MaxBlobCount = 72;
        TargetBlobCount = 48;
        BlobBaseFeeUpdateFraction = 40061729;
        Released = false;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new BPO5());
}
