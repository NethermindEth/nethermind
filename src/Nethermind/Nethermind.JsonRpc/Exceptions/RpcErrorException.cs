// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Exceptions;

/// <summary>A module-selected JSON-RPC error raised during invocation or deferred execution.</summary>
internal sealed class RpcErrorException(int code, string message) : Exception(message)
{
    internal int Code { get; } = code;
}
