// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class BPO3 : BPO2
{
    private static IReleaseSpec _instance;

    public BPO3()
    {
        Name = "bpo3";
        MaxBlobCount = 32;
        TargetBlobCount = 21;
        // change
        BlobBaseFeeUpdateFraction = 11684671;
        Released = false;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new BPO3());
}
