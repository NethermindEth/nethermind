// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs;

namespace Nethermind.Optimism;

public class OptimismReleaseSpec : ReleaseSpec, IOptimismReleaseSpec
{
    public bool IsOpGraniteEnabled { get; set; }
    public bool IsOpHoloceneEnabled { get; set; }
    public bool IsOpIsthmusEnabled { get; set; }
    public bool IsOpJovianEnabled { get; set; }
    public bool IsOpKarstEnabled { get; set; }
}
