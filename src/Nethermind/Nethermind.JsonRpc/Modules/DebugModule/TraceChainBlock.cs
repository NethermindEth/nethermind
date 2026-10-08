// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.DebugModule;

internal sealed record TraceChainBlock(
    [property: JsonPropertyName("block")] ulong Block,
    [property: JsonPropertyName("hash")] Hash256 Hash,
    [property: JsonPropertyName("traces")] TraceChainTransaction?[] Traces);

internal sealed record TraceChainTransaction(
    [property: JsonPropertyName("txHash")] Hash256 TxHash,
    [property: JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GethLikeTxTrace? Result,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error);
