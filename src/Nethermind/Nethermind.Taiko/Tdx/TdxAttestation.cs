// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core.Crypto;

namespace Nethermind.Taiko.Tdx;

/// <summary>
/// TDX signed block header containing the signature, block hash, state root and header.
/// Signature is over keccak(<blockHash><stateRoot>).
/// </summary>
public class TdxBlockHeaderSignature
{
    public required byte[] Signature { get; init; }
    public required Hash256 BlockHash { get; init; }
    public required Hash256 StateRoot { get; init; }
    public required BlockHeaderForRpc Header { get; init; }
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

