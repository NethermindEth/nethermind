// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.JsonRpc.Data;

public readonly struct AccountInfoForRpc
{
    public static readonly AccountInfoForRpc Empty = new();
    public AccountInfoForRpc()
    {
    }

    public byte[] Code { get; init; } = [];
    public UInt256 Balance { get; init; } = default;
    public UInt256 Nonce { get; init; } = default;
}
