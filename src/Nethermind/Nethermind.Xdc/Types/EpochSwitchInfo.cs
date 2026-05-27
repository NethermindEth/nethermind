// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System;
using System.Collections.Generic;

namespace Nethermind.Xdc.Types;

public class EpochSwitchInfo(Address[] masternodes, Address[] StandbyNodes, Address[] penalties, BlockRoundInfo epochSwitchCurrentBlockInfo) : IEquatable<EpochSwitchInfo>
{
    public Address[] Masternodes { get; set; } = masternodes;
    public Address[] StandbyNodes { get; } = StandbyNodes;
    public Address[] Penalties { get; set; } = penalties;
    public BlockRoundInfo EpochSwitchBlockInfo { get; set; } = epochSwitchCurrentBlockInfo;
    public BlockRoundInfo? EpochSwitchParentBlockInfo { get; set; }

    public bool Equals(EpochSwitchInfo? other) =>
        other is not null &&
        Masternodes.AsSpan().SequenceEqual(other.Masternodes) &&
        StandbyNodes.AsSpan().SequenceEqual(other.StandbyNodes) &&
        Penalties.AsSpan().SequenceEqual(other.Penalties) &&
        EqualityComparer<BlockRoundInfo>.Default.Equals(EpochSwitchBlockInfo, other.EpochSwitchBlockInfo) &&
        EqualityComparer<BlockRoundInfo>.Default.Equals(EpochSwitchParentBlockInfo, other.EpochSwitchParentBlockInfo);

    public override bool Equals(object? obj) => Equals(obj as EpochSwitchInfo);

    public override int GetHashCode()
    {
        HashCode hashCode = new();
        hashCode.Add(Masternodes.Length);
        foreach (Address masternode in Masternodes)
        {
            hashCode.Add(masternode);
        }

        hashCode.Add(StandbyNodes.Length);
        foreach (Address standbyNode in StandbyNodes)
        {
            hashCode.Add(standbyNode);
        }

        hashCode.Add(Penalties.Length);
        foreach (Address penalty in Penalties)
        {
            hashCode.Add(penalty);
        }

        hashCode.Add(EpochSwitchBlockInfo);
        hashCode.Add(EpochSwitchParentBlockInfo);
        return hashCode.ToHashCode();
    }
}
