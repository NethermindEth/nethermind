// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.LightClient;

internal sealed record VerifiedHead(ulong Slot, ulong Number, Hash256 BlockHash, Hash256 StateRoot);

internal sealed class RpcException(int code, string message, string? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public string? RevertData { get; } = data;
}
