// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Shutter;

public readonly struct ShutterTransactions
{
    public Transaction[] Transactions { get; init; }
    public ulong Slot { get; init; }
}
