// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Fork7805 : Prague
{
    private static IReleaseSpec _instance;

    protected Fork7805()
    {
        Name = "Fork7805";
        IsEip7805Enabled = true;
    }

    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new Fork7805());
}
