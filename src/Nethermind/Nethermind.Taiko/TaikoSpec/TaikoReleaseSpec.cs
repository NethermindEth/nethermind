// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using Nethermind.Core;
using Nethermind.Specs;

namespace Nethermind.Taiko.TaikoSpec;

public class TaikoReleaseSpec : ReleaseSpec, ITaikoReleaseSpec
{
    public bool IsOntakeEnabled { get; set; }
    public bool IsPacayaEnabled { get; set; }
    public bool IsShastaEnabled { get; set; }
    public bool UseSurgeGasPriceOracle { get; set; }
    public required Address TaikoL2Address { get; set; }
    public bool IsRip7728Enabled { get; set; }
    public bool IsL1StaticCallEnabled { get; set; }

    public override FrozenSet<AddressAsKey> BuildPrecompilesCache() =>
        ITaikoReleaseSpec.BuildTaikoPrecompilesCache(base.BuildPrecompilesCache(), IsRip7728Enabled, IsL1StaticCallEnabled);
}
