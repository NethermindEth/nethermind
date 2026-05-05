// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Taiko.Precompiles;

public class L1PrecompileMetrics
{
    [CounterMetric]
    [Description("Number of L1SLOAD precompile calls.")]
    public static long L1SloadPrecompile { get; set; }

    [CounterMetric]
    [Description("Number of L1STATICCALL precompile calls.")]
    public static long L1StaticCallPrecompile { get; set; }
}
