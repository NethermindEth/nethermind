// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.Bft;

/// <summary>Polarity of a validator membership vote.</summary>
public enum VoteType : byte
{
    Add,
    Drop
}

/// <summary>A validator membership vote as embedded in a block's extra data.</summary>
/// <param name="Recipient">The account being voted in or out.</param>
/// <param name="Type">Whether the vote adds or drops the recipient.</param>
public sealed record Vote(Address Recipient, VoteType Type)
{
    /// <summary>Wire value for an add vote; a drop vote is encoded as an empty RLP string (QBFT) or <c>0x00</c> (IBFT2).</summary>
    public const byte AddByteValue = 0xFF;
    public const byte DropByteValue = 0x00;

    public bool IsAdd => Type == VoteType.Add;
    public bool IsDrop => Type == VoteType.Drop;

    public static Vote AuthVote(Address address) => new(address, VoteType.Add);
    public static Vote DropVote(Address address) => new(address, VoteType.Drop);
}

/// <summary>A vote extracted from a block header together with the proposer that cast it.</summary>
public sealed record ValidatorVote(VoteType Type, Address Proposer, Address Recipient)
{
    public bool IsAuthVote => Type == VoteType.Add;
}
