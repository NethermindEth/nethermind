// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Avalanche;

/// <summary>Per-fork release spec flags specific to the Avalanche C-Chain.</summary>
public interface IAvalancheReleaseSpec : IReleaseSpec
{
    bool IsApricotPhase3Enabled { get; }
    bool IsDurangoEnabled { get; }
    bool IsEtnaEnabled { get; }
    bool IsFortunaEnabled { get; }
    bool IsGraniteEnabled { get; }
}
