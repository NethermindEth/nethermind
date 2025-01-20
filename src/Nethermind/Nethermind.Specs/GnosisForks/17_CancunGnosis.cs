// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.GnosisForks;

public class CancunGnosis : Forks.Cancun
{

    private static IReleaseSpec _instance;
    protected CancunGnosis() : base()
    {
        MaxBlobCount = 2;
        TargetBlobCount = 1;
        BlobBaseFeeUpdateFraction = 1112826;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new CancunGnosis());
}
