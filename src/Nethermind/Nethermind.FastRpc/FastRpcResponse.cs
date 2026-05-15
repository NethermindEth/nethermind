// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.FastRpc;

/// <summary>
/// Pre-encoded response bodies for JSON and SSZ transports.
/// </summary>
/// <param name="json">A JSON value ready to be embedded as a REST or JSON-RPC result body.</param>
/// <param name="ssz">An SSZ body for REST SSZ responses.</param>
public readonly struct FastRpcResponse(ReadOnlyMemory<byte> json, ReadOnlyMemory<byte> ssz)
{
    /// <summary>
    /// JSON response bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Json { get; } = json;

    /// <summary>
    /// SSZ response bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Ssz { get; } = ssz;
}
