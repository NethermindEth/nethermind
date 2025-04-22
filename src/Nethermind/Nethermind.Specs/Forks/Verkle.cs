// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Verkle: Shanghai
{
    private static IReleaseSpec _instance;

    protected Verkle()
    {
        Name = "Verkle";
        IsEip4762Enabled = true;
        IsEip6800Enabled = true;
        IsEip2935Enabled = true;
        Eip2935ContractAddress = new("0xfffffffffffffffffffffffffffffffffffffffe");
        IsEip6780Enabled = true;
        IsEip7709Enabled = true;
    }
    public new static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new Verkle());
}
