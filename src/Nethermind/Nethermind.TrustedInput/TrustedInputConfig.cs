// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TrustedInput;

/// <inheritdoc/>
public class TrustedInputConfig : ITrustedInputConfig
{
    /// <inheritdoc/>
    public bool Enabled { get; set; }
    /// <inheritdoc/>
    public int CacheSize { get; set; } = 64;
    /// <inheritdoc/>
    public string? OutputDirectory { get; set; }
}
