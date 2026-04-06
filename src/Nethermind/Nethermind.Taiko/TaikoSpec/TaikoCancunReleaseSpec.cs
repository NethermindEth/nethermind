// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Specs.Forks;

namespace Nethermind.Taiko.TaikoSpec;

public class TaikoOntakeReleaseSpec : Cancun, ITaikoReleaseSpec
{
    public TaikoOntakeReleaseSpec() => IsOntakeEnabled = true;

    public bool IsOntakeEnabled { get; set; }
    public bool IsPacayaEnabled { get; set; }
    public bool IsShastaEnabled { get; set; }
    public bool UseSurgeGasPriceOracle { get; set; }
    public Address TaikoL2Address { get; set; } = new("0x1670000000000000000000000000000000010001");
    public bool IsRip7728Enabled { get; set; }
    public bool IsL1StaticCallEnabled { get; set; }

    public override FrozenSet<AddressAsKey> BuildPrecompilesCache()
    {
        HashSet<AddressAsKey> cache = new(base.BuildPrecompilesCache());
        if (IsRip7728Enabled) cache.Add(L1SloadPrecompile.Address);
        if (IsL1StaticCallEnabled) cache.Add(L1StaticCallPrecompile.Address);
        return cache.ToFrozenSet();
    }
}

public class TaikoPacayaReleaseSpec : TaikoOntakeReleaseSpec
{
    public TaikoPacayaReleaseSpec() => IsPacayaEnabled = true;
}

public class TaikoShastaReleaseSpec : TaikoPacayaReleaseSpec
{
    public TaikoShastaReleaseSpec() => IsShastaEnabled = true;
}
