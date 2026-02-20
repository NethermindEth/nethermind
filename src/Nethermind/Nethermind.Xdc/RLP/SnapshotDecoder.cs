// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal sealed class SnapshotDecoder : BaseSnapshotDecoder<Snapshot>
{
    protected override Snapshot DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    => DecodeBase<Snapshot>(ref decoderContext, (number, hash, candidates) => new Snapshot(number, hash, candidates), rlpBehaviors);

    protected override Snapshot DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    => DecodeBase<Snapshot>(rlpStream, (number, hash, candidates) => new Snapshot(number, hash, candidates), rlpBehaviors);
}
