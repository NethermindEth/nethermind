// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Barnet : Cancun
{
    private static IReleaseSpec _instance;

    protected Barnet()
    {
        Name = nameof(Barnet);
        IsGnosisBlobFeeCollectionEnabled = true;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Barnet());
}
