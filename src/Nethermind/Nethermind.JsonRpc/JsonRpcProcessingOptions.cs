// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc;

/// <summary>Provides transport-level options for JSON-RPC request processing.</summary>
/// <param name="InputMode">Defines whether one or many top-level JSON documents are accepted.</param>
public readonly record struct JsonRpcProcessingOptions(JsonRpcInputMode InputMode);
