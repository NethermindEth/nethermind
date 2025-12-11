// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Taiko.Tdx;

/// <summary>
/// Service for generating TDX attestations for blocks.
/// </summary>
public interface ITdxService
{
    /// <summary>
    /// Whether the service is available (bootstrapped and socket accessible).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Bootstrap the TDX service: generate key, get initial quote.
    /// </summary>
    TdxGuestInfo Bootstrap();

    /// <summary>
    /// Get guest info if already bootstrapped.
    /// </summary>
    TdxGuestInfo? GetGuestInfo();

    /// <summary>
    /// Generate a TDX attestation for a block.
    /// </summary>
    /// <param name="block">The block to attest.</param>
    TdxAttestation Attest(Block block);
}

