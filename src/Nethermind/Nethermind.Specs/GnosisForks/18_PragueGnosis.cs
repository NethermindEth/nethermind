// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs.GnosisForks;

public class PragueGnosis : Prague
{
    private static IReleaseSpec _instance;

    private PragueGnosis()
        => ToGnosisFork(this);

    public static void ToGnosisFork(Prague spec)
    {
        CancunGnosis.ToGnosisFork(spec);
        spec.IsEip4844FeeCollectorEnabled = true;
        spec.BlobBaseFeeUpdateFraction = 0x10fafa;
        spec.TargetBlobCount = 1;
        spec.MaxBlobCount = 2;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new PragueGnosis());
}
