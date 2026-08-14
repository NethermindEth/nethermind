// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>What a node advertises about the state history it serves, so a consumer can tell an archive server
/// apart from an ordinary node - and reject one it cannot use - before spending a dial and a handshake on it.
/// Only session-stable facts belong here: the served watermark moves every few seconds and would force the record
/// to be re-signed constantly, so it stays in the protocol handshake instead.</summary>
public readonly record struct HistoryServingAdvertisement(byte RowFormatVersion, bool ServesFullArchive)
{
    public override string ToString() => $"row format {RowFormatVersion}, {(ServesFullArchive ? "full archive" : "bounded window")}";
}

public class NHistEntry(byte rowFormatVersion, bool servesFullArchive)
    : EnrContentEntry<HistoryServingAdvertisement>(new HistoryServingAdvertisement(rowFormatVersion, servesFullArchive))
{
    public override string Key => EnrContentKey.NHist;

    protected override int GetRlpLengthOfValue() => Rlp.LengthOfSequence(ContentLength);

    protected override void EncodeValue<TWriter>(ref TWriter writer)
    {
        writer.StartSequence(ContentLength);
        writer.Encode(Value.RowFormatVersion);
        writer.Encode(Value.ServesFullArchive);
    }

    private int ContentLength => Rlp.LengthOf(Value.RowFormatVersion) + Rlp.LengthOf(Value.ServesFullArchive);
}
