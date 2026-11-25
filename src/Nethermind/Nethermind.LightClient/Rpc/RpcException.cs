// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.LightClient.Rpc;

internal sealed class RpcException(int code, string message, string? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public string? RevertData { get; } = data;
}
