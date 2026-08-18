// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TxPool;

namespace Nethermind.Xdc.TxPool;

internal static class XdcAcceptTxResult
{
    private const int BlackListedAddressId = 1000;

    public static AcceptTxResult BlackListedAddress { get; } = new(BlackListedAddressId, nameof(BlackListedAddress));
}
