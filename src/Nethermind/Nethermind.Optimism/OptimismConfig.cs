// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Optimism;

public class OptimismConfig : IOptimismConfig
{
    /// <inheritdoc/>
    /// <remarks>A blank value is stored as <see langword="null"/>, so consumers treat it as unset rather than as a URL.</remarks>
    public string? SequencerUrl { get; set => field = string.IsNullOrWhiteSpace(value) ? null : value; }
    public bool ClEnabled { get; set; } = false;
    public string? ClP2PHost { get; set; } = null;
    public int ClP2PPort { get; set; } = 3030;
    public string? L1BeaconApiEndpoint { get; set; }
    public string? L1EthApiEndpoint { get; set; }
}
