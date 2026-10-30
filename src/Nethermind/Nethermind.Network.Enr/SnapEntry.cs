// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// An entry listing the snap protocol versions the node serves, so DNS list tooling such as
/// <c>devp2p nodeset filter -snap</c> selects the node. An empty list means snap/1 only.
/// https://github.com/ethereum/devp2p/issues/286
/// </summary>
public class SnapEntry(params int[] versions) : EnrContentEntry<int[]>(versions)
{
    public override string Key => EnrContentKey.Snap;

    protected override int GetRlpLengthOfValue() => Rlp.LengthOfSequence(GetContentLength());

    protected override void EncodeValue<TWriter>(ref TWriter writer)
    {
        writer.StartSequence(GetContentLength());
        foreach (int version in Value)
        {
            writer.Encode(version);
        }
    }

    private int GetContentLength()
    {
        int length = 0;
        foreach (int version in Value)
        {
            length += Rlp.LengthOf(version);
        }

        return length;
    }
}
