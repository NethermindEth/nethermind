// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs;

namespace Nethermind.Avalanche;

public class AvalancheReleaseSpec : ReleaseSpec, IAvalancheReleaseSpec
{
    public bool IsApricotPhase3Enabled { get; set; }
    public bool IsDurangoEnabled { get; set; }
    public bool IsEtnaEnabled { get; set; }
    public bool IsFortunaEnabled { get; set; }
    public bool IsGraniteEnabled { get; set; }
}
