// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Facade.Eth.RpcTransaction;

namespace Nethermind.JsonRpc.Data;

public class SignTransactionResult : IDisposable
{
    [JsonPropertyName("raw")]
    public required ArrayPoolList<byte> Raw { get; init; }

    [JsonPropertyName("tx")]
    public required TransactionForRpc Tx { get; init; }

    public void Dispose() => Raw.Dispose();
}
