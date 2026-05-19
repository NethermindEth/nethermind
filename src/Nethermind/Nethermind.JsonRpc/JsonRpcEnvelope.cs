// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.JsonRpc;

internal readonly struct JsonRpcEnvelope(
    string? jsonRpc,
    JsonRpcId id,
    string? method,
    bool hasParams,
    JsonValueKind paramsKind,
    int paramsStart,
    int paramsLength)
{
    public string? JsonRpc { get; } = jsonRpc;
    public JsonRpcId Id { get; } = id;
    public string? Method { get; } = method;
    public bool HasParams { get; } = hasParams;
    public JsonValueKind ParamsKind { get; } = paramsKind;
    public int ParamsStart { get; } = paramsStart;
    public int ParamsLength { get; } = paramsLength;
}
