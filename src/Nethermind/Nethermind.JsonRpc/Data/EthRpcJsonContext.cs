// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Serialization.Json;
using Nethermind.State.Proofs;

namespace Nethermind.JsonRpc.Data;

/// <summary>
/// Metadata for eth, debug and trace payloads, and for those method signatures do not name: <c>eth_config</c> builds its result
/// as nodes, proofs are written by a converter, peer protocols are stored as <see cref="object"/>, diagnostics wrap responses,
/// the client builds its own requests, and results are written by their runtime collection types.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
// eth_ types
[JsonSerializable(typeof(AccountOverride))]
[JsonSerializable(typeof(AccountProof))]
// debug_ types
[JsonSerializable(typeof(GethLikeTxTrace))]
[JsonSerializable(typeof(GethTraceOptions))]
// trace_ types
[JsonSerializable(typeof(ParityLikeTxTrace))]
// payloads method signatures do not name
[JsonSerializable(typeof(ForkConfigSummary))]
[JsonSerializable(typeof(ForkConfig))]
[JsonSerializable(typeof(BlobScheduleSettingsForRpc))]
[JsonSerializable(typeof(OrderedDictionary<string, Address>))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(StorageProof))]
[JsonSerializable(typeof(StorageProof[]))]
[JsonSerializable(typeof(ReadOnlyMemory<byte>?))]
[JsonSerializable(typeof(JsonRpcDiagnostics.DiagnosticJsonRpcResult))]
[JsonSerializable(typeof(RpcReport))]
[JsonSerializable(typeof(Client.JsonRpcClientRequest))]
[JsonSerializable(typeof(LogEntryForRpc[]))]
[JsonSerializable(typeof(Modules.Eth.BadBlock[]))]
[JsonSerializable(typeof(Modules.DebugModule.GethLikeTxTraceStreamingResult))]
[JsonSerializable(typeof(Modules.Trace.ParityTxTraceFromReplay[]))]
[JsonSerializable(typeof(Modules.Trace.ParityTxTraceFromStore[]))]
[JsonSerializable(typeof(Nethermind.Core.Collections.ArrayPoolList<Modules.Trace.ParityTxTraceFromStore>))]
[JsonSerializable(typeof(Nethermind.Core.Collections.ArrayPoolList<Nethermind.Facade.Filters.FilterLog>))]
[JsonSerializable(typeof(Nethermind.Core.Collections.ArrayPoolList<Nethermind.Core.Crypto.Hash256>))]
[JsonSerializable(typeof(ReceiptsForRpc<ReceiptForRpc>))]
[JsonSerializable(typeof(Modules.Admin.PeerInfo.ProtocolVersion))]
internal partial class EthRpcJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.EthRpc);
}
