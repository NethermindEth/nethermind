// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TrustedInput;

public class TrustedInputConfig : ITrustedInputConfig
{
    public bool Enabled { get; set; }
    public int CacheSize { get; set; } = 64;
    public string? OutputDirectory { get; set; }
}
