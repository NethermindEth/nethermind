// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Taiko.Tdx;

/// <summary>
/// Service for generating TDX attestations for blocks.
/// </summary>
public interface ITdxService
{
    /// <summary>
    /// Whether the service is bootstrapped.
    /// </summary>
    bool IsBootstrapped { get; }

    /// <summary>
    /// Bootstrap the TDX service: generate key, get initial quote.
    /// </summary>
    TdxGuestInfo Bootstrap();

    /// <summary>
    /// Get guest info if already bootstrapped.
    /// </summary>
    TdxGuestInfo? GetGuestInfo();

    /// <summary>
    /// Generate a TDX attestation for a block hash.
    /// </summary>
    /// <param name="blockHash">The block hash to attest.</param>
    BlockHashTdxAttestation AttestBlockHash(Hash256 blockHash);

    /// <summary>
    /// Generate a TDX attestation for a block header.
    /// </summary>
    /// <param name="blockHeader">The block header to attest.</param>
    BlockHeaderTdxAttestation AttestBlockHeader(BlockHeader blockHeader);
}
