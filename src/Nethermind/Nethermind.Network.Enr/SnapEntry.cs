// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// An entry advertising that the node serves the snap protocol. Its value is an empty list,
/// matching go-ethereum, so DNS list tooling such as <c>devp2p nodeset filter -snap</c> selects the node.
/// </summary>
public class SnapEntry : EnrContentEntry<object?>
{
    private SnapEntry() : base(null) { }

    public static SnapEntry Instance { get; } = new();

    public override string Key => EnrContentKey.Snap;

    protected override int GetRlpLengthOfValue() => Rlp.LengthOfSequence(0);

    protected override void EncodeValue<TWriter>(ref TWriter writer) => writer.StartSequence(0);
}
