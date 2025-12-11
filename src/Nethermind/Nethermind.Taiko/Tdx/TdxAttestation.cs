// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.Taiko.Tdx;

/// <summary>
/// TDX attestation result containing proof and quote.
/// </summary>
public class TdxAttestation
{
    /// <summary>
    /// The proof bytes: instance_id (4) + address (20) + signature (65) = 89 bytes.
    /// </summary>
    public required byte[] Proof { get; init; }

    /// <summary>
    /// The TDX quote bytes.
    /// </summary>
    public required byte[] Quote { get; init; }

    /// <summary>
    /// The instance hash that was signed and quoted.
    /// </summary>
    public required Hash256 InstanceHash { get; init; }
}

/// <summary>
/// TDX guest information for instance registration and bootstrap persistence.
/// </summary>
public class TdxGuestInfo
{
    public required string IssuerType { get; init; }
    public required string PublicKey { get; init; }
    public required string Quote { get; init; }
    public required string Nonce { get; init; }
    public JsonElement? Metadata { get; init; }
}

