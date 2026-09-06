// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.JsonRpc;

internal readonly struct JsonRpcEnvelope(
    string? jsonRpc,
    in JsonRpcId id,
    string? method,
    bool hasParams,
    JsonValueKind paramsKind,
    int paramsStart,
    int paramsLength)
{
    public readonly string? JsonRpc = jsonRpc;
    public readonly JsonRpcId Id = id;
    public readonly string? Method = method;
    public readonly bool HasParams = hasParams;
    public readonly JsonValueKind ParamsKind = paramsKind;
    public readonly int ParamsStart = paramsStart;
    public readonly int ParamsLength = paramsLength;
}
