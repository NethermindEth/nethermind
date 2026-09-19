// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>
/// The Fulu <c>cgc</c> ENR entry (p2p-interface.md): "Custody group count, Uint64 big endian
/// integer with no leading zero bytes (0 encoded as empty byte string)".
/// </summary>
/// <remarks>
/// Shaped like <see cref="Eth2Entry"/>, but <c>public</c> rather than <c>internal</c> so its
/// encoding can be unit-tested directly: this project has no <c>InternalsVisibleTo</c> for its test
/// assembly, and adding one would mean editing a file outside this change's allowed boundary.
/// RLP's own unsigned-integer encoding already is "big-endian, no leading zero bytes, 0 as an empty
/// string", so <c>writer.Encode(ulong)</c> alone satisfies the spec's byte-encoding rule without any
/// extra trimming here. Not wired into <see cref="BeaconNodeRecordProvider"/> by this change: that
/// record builder is shared with concurrent Gloas ENR work (the <c>nfd</c> entry), so wiring is left
/// to whichever change lands second, to avoid a same-file collision - see 'deviations'.
/// </remarks>
public sealed class CustodyGroupCountEntry(ulong custodyGroupCount) : EnrContentEntry<ulong>(custodyGroupCount)
{
    public override string Key => "cgc";

    protected override int GetRlpLengthOfValue() => Rlp.LengthOf(Value);

    protected override void EncodeValue<TWriter>(ref TWriter writer) => writer.Encode(Value);
}
