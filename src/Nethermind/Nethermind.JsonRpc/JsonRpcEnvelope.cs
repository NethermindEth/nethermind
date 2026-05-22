// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.JsonRpc;

internal readonly record struct JsonRpcEnvelope(
    string? JsonRpc,
    JsonRpcId Id,
    string? Method,
    bool HasParams,
    JsonValueKind ParamsKind,
    int ParamsStart,
    int ParamsLength);
