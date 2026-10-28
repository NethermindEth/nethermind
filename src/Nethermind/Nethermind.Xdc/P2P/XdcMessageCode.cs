// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Xdc.P2P;

public static class XdcMessageCode
{
    /// <summary>TomoX orderbook order broadcast. Occupies the code upstream uses for NewPooledTransactionHashes.</summary>
    public const int OrderTx = 0x08;

    /// <summary>TomoX lending order broadcast. Occupies the code upstream uses for GetPooledTransactions.</summary>
    public const int LendingTx = 0x09;

    public const int VoteMsg = 0xe0;
    public const int TimeoutMsg = 0xe1;
    public const int SyncInfoMsg = 0xe2;

    /// <summary>EIP-2464 announcement, relocated from <c>0x08</c> because that code carries <see cref="OrderTx"/>.</summary>
    public const int NewPooledTransactionHashes = 0xe3;

    /// <summary>EIP-2464 request, relocated from <c>0x09</c> because that code carries <see cref="LendingTx"/>.</summary>
    public const int GetPooledTransactions = 0xe4;

    /// <summary>EIP-2464 response, relocated from <c>0x0a</c> to stay adjacent to the two messages above.</summary>
    public const int PooledTransactions = 0xe5;
}
