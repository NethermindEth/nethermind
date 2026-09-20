// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>
/// The Fulu <c>nfd</c> ("next fork digest") ENR entry (EIP-7892 / p2p-interface.md): the raw 4-byte
/// SSZ <c>ForkDigest</c> of the next scheduled fork, regular or blob-parameter-only, so peers can
/// evaluate an upcoming digest rotation without waiting to observe it on the wire.
/// </summary>
/// <remarks>
/// Shaped like <see cref="Eth2Entry"/>: raw bytes carried as an RLP byte string, since <c>ForkDigest</c>
/// is a fixed-length SSZ vector whose SSZ encoding is exactly its 4 bytes with no length prefix.
/// Per spec, when no next fork is scheduled the entry carries the type's zero default
/// (<see cref="NoneScheduled"/>) rather than being omitted. Published in the local record by
/// <see cref="BeaconNodeRecordProvider"/>, computed via <see cref="EnrForkId.NextForkDigest"/> so it
/// stays derived from the same fork schedule as the <c>eth2</c> entry's <c>next_fork_epoch</c>.
/// </remarks>
internal sealed class NfdEntry(byte[] nextForkDigest) : EnrContentEntry<byte[]>(nextForkDigest)
{
    /// <summary>The SSZ <c>ForkDigest</c> zero default, advertised when no next fork is scheduled.</summary>
    public static readonly byte[] NoneScheduled = new byte[4];

    public override string Key => "nfd";

    protected override int GetRlpLengthOfValue() => Rlp.LengthOf(Value);

    protected override void EncodeValue<TWriter>(ref TWriter writer) => writer.Encode(Value);
}
