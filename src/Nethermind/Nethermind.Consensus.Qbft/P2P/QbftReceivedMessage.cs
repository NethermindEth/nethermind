// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.P2P;

/// <summary>A raw <c>istanbul/100</c> message as received from a peer.</summary>
/// <param name="Code">Message code, one of <see cref="Messages.QbftMessageCode"/>.</param>
/// <param name="Data">The RLP payload as sent.</param>
/// <param name="SenderAddress">Address of the peer's node key, used to avoid gossiping the message back.</param>
public sealed record QbftReceivedMessage(int Code, byte[] Data, Address SenderAddress)
{
    public int Size => Data.Length;
}
