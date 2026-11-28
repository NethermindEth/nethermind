// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;

/// <summary>One announced EIP-8518 proof wrapper: its canonical length, hash and coverage hints.</summary>
public readonly record struct WrapperAnnouncement(int Length, ValueHash256 Hash, ValueHash256 DepsHash, ValueHash256 TxsHash);

/// <summary>EIP-8518 <c>NewProofWrapperHashes</c>: 1 to 64 held, validated wrappers in strictly ascending hash order.</summary>
public sealed class NewProofWrapperHashesMessage(IReadOnlyList<WrapperAnnouncement> announcements) : P2PMessage
{
    public override int PacketType => Eth76MessageCode.NewProofWrapperHashes;
    public override string Protocol => "eth";

    public IReadOnlyList<WrapperAnnouncement> Announcements { get; } = announcements;

    public override string ToString() => $"{nameof(NewProofWrapperHashesMessage)}({Announcements.Count})";
}
