// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt;

public class PbtConfig : IPbtConfig
{
    public bool Enabled { get; set; }
    public int CompactSize { get; set; } = 32;
    public int MinReorgDepth { get; set; } = 128;
    public int MaxReorgDepth { get; set; } = 256;
    public bool ImportFromPreimageFlat { get; set; }
}
