// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TxPool;

namespace Nethermind.Xdc.TxPool;

internal static class XdcAcceptTxResult
{
    private const int BlackListedAddressId = 1000;
    private const string BlackListedAddressCode = "BlackListedAddress";

    public static AcceptTxResult BlackListedSender { get; } = new(BlackListedAddressId, BlackListedAddressCode, "Transaction sender is blacklisted");
    public static AcceptTxResult BlackListedRecipient { get; } = new(BlackListedAddressId, BlackListedAddressCode, "Transaction recipient is blacklisted");
}
