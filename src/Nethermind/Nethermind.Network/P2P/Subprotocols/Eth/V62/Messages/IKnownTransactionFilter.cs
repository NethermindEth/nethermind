// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;

/// <summary>Settles a gossiped transaction that is already known, from its hash alone.</summary>
internal interface IKnownTransactionFilter
{
    /// <returns>Whether the transaction was settled, so the caller skips it without decoding.</returns>
    bool TrySettleKnown(in ValueHash256 hash);
}
