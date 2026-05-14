// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Facade.Eth.RpcTransaction;

namespace Nethermind.JsonRpc.Data;

public class SignTransactionResult
{
    [JsonPropertyName("raw")]
    public required byte[] Raw { get; init; }

    [JsonPropertyName("tx")]
    public required TransactionForRpc Tx { get; init; }
}
