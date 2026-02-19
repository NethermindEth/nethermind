// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Taiko.Tdx;

/// <summary>
/// Client for communicating with the tdxs daemon via Unix socket.
/// </summary>
public interface ITdxsClient
{
    /// <summary>
    /// Issues a TDX attestation quote.
    /// </summary>
    /// <param name="userData">User data to embed in the quote (typically 32 bytes).</param>
    /// <param name="nonce">Random nonce for freshness.</param>
    /// <returns>The attestation document (quote) bytes.</returns>
    byte[] Issue(byte[] userData, byte[] nonce);

    /// <summary>
    /// Gets metadata about the TDX environment.
    /// </summary>
    TdxMetadata GetMetadata();
}

public class TdxMetadata
{
    public required string IssuerType { get; init; }
    public JsonElement? Metadata { get; init; }
}

