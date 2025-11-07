// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class BPO4 : BPO3
{
    private static IReleaseSpec _instance;

    public BPO4()
    {
        Name = "bpo4";
        MaxBlobCount = 48;
        TargetBlobCount = 32;
        // change
        BlobBaseFeeUpdateFraction = 11684671;
        Released = false;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new BPO4());
}
