// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.StateGas;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing;

/// <summary>
/// Metadata for tracer output that RPC signatures expose only as <see cref="object"/>, so no generated RPC context roots it,
/// and for the traces and receipts the trace store and trace dumps write.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(GethTraceOptionsConverter.WireOptions))]
[JsonSerializable(typeof(GethLikeCustomTrace))]
[JsonSerializable(typeof(GethLikeCustomTrace.EmptyValue))]
[JsonSerializable(typeof(GethLikeTxTraceCollection))]
[JsonSerializable(typeof(NativeCallTracerCallFrame))]
[JsonSerializable(typeof(NativeCallTracerConfig))]
[JsonSerializable(typeof(NativeCallTracerLogEntry))]
[JsonSerializable(typeof(NativePrestateTracerAccount))]
[JsonSerializable(typeof(NativePrestateTracerConfig))]
[JsonSerializable(typeof(NativePrestateTracerDiffMode))]
[JsonSerializable(typeof(Dictionary<AddressAsKey, NativePrestateTracerAccount>))]
[JsonSerializable(typeof(StateGasTrace))]
[JsonSerializable(typeof(ParityVmOperationTrace))]
[JsonSerializable(typeof(IReadOnlyList<ParityVmOperationTrace>))]
[JsonSerializable(typeof(RenderedJson))]
[JsonSerializable(typeof(GethTxFileTraceEntry))]
[JsonSerializable(typeof(List<GethTxTraceEntry>))]
[JsonSerializable(typeof(GethLikeBlockFileTracer.FileTraceSummary))]
[JsonSerializable(typeof(List<ParityLikeTxTrace>))]
[JsonSerializable(typeof(IReadOnlyCollection<ParityLikeTxTrace>))]
[JsonSerializable(typeof(IReadOnlyCollection<GethLikeTxTrace>))]
[JsonSerializable(typeof(GethLikeTxTrace[][]))]
[JsonSerializable(typeof(TxReceipt[]))]
[JsonSerializable(typeof(UInt256?))]
[JsonSerializable(typeof(byte[][]))]
[JsonSerializable(typeof(Memory<NativeCallTracerLogEntry>))]
[JsonSerializable(typeof(Dictionary<Hash256, byte[]>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, System.Text.Json.JsonElement>))]
[JsonSerializable(typeof(Dictionary<UInt256, UInt256>))]
internal partial class TracingJsonContext : JsonSerializerContext
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Registers the metadata before any code in this assembly serializes.")]
    internal static void Register() => EthereumJsonSerializer.AddTypeInfoResolver(Default, JsonTypeInfoResolverPriority.EthRpc);
}
