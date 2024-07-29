// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Optimism;

public class OptimismConfig : IOptimismConfig
{
    public string? SequencerUrl { get; set; } = null;
    public bool SequencerMode { get; set; } = false;
}
