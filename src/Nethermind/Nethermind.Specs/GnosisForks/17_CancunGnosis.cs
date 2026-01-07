// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs.GnosisForks;

public class CancunGnosis : Cancun
{
    private static IReleaseSpec? _instance;

    private CancunGnosis()
        => ToGnosisFork(this);

    public static void ToGnosisFork(Cancun spec)
    {
        LondonGnosis.ToGnosisFork(spec);
        spec.MaxBlobCount = 2;
        spec.TargetBlobCount = 1;
        spec.BlobBaseFeeUpdateFraction = 1112826;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new CancunGnosis());
}
