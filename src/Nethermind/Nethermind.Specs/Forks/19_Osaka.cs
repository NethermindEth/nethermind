// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Osaka : Prague
{
    private static IReleaseSpec _instance;

    public Osaka()
    {
        Name = "Osaka";
        IsEip7594Enabled = true;
        IsEip7823Enabled = true;
        IsEip7883Enabled = true;
        // IsEip7825Enabled = true;
        IsEip7918Enabled = true;
        Released = false;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new Osaka());
}
