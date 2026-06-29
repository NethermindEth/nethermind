// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Avalanche;

/// <summary>Per-fork release-spec flags specific to the Avalanche C-Chain (Coreth) upgrade schedule.</summary>
public interface IAvalancheReleaseSpec : IReleaseSpec
{
    bool IsApricotPhase2Enabled { get; } // Berlin EVM
    bool IsApricotPhase3Enabled { get; } // London / EIP-1559
    bool IsDurangoEnabled { get; }       // Shanghai EVM (no withdrawals)
    bool IsEtnaEnabled { get; }          // Cancun EVM subset (no blobs)
    bool IsFortunaEnabled { get; }       // ACP-176 dynamic fee
    bool IsGraniteEnabled { get; }       // P256VERIFY precompile
}
