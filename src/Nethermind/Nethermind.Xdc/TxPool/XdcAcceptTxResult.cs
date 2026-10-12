// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TxPool;

namespace Nethermind.Xdc.TxPool;

internal static class XdcAcceptTxResult
{
    // One declared result, so the two verdicts stay equal to each other and differ only in their message.
    // Must stay above the properties: static initializers run in textual order, and read too early this
    // would be default(AcceptTxResult), which is Accepted.
    private static readonly AcceptTxResult BlackListedAddress = new("BlackListedAddress");

    public static AcceptTxResult BlackListedSender { get; } = BlackListedAddress.WithMessage("Transaction sender is blacklisted");
    public static AcceptTxResult BlackListedRecipient { get; } = BlackListedAddress.WithMessage("Transaction recipient is blacklisted");
}
