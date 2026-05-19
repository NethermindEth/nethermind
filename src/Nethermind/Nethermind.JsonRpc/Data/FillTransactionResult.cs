// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Facade.Eth.RpcTransaction;

namespace Nethermind.JsonRpc.Data;

/// <summary>
/// Result of <c>eth_fillTransaction</c>: the unsigned RLP-encoded transaction and its
/// echoed object form with defaults filled in.
/// </summary>
public class FillTransactionResult
{
    /// <summary>RLP encoding of the unsigned transaction (typed-tx prefix included for non-legacy types).</summary>
    [JsonPropertyName("raw")]
    public required byte[] Raw { get; init; }

    /// <summary>Filled transaction echo. Signature fields are zero; signing is the caller's responsibility.</summary>
    [JsonPropertyName("tx")]
    public required TransactionForRpc Tx { get; init; }
}
