// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal sealed class SubnetSnapshotDecoder : BaseSnapshotDecoder<SubnetSnapshot>
{

    protected override SubnetSnapshot DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        SubnetSnapshot subnetSnapshot = DecodeBase<SubnetSnapshot>(ref decoderContext, (number, hash, candidates) => new SubnetSnapshot(number, hash, candidates), rlpBehaviors);
        if (subnetSnapshot is null)
            return null;

        subnetSnapshot.NextEpochPenalties = DecodeAddressArray(ref decoderContext);
        return subnetSnapshot;
    }

    protected override SubnetSnapshot DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        SubnetSnapshot subnetSnapshot = DecodeBase<SubnetSnapshot>(rlpStream, (number, hash, candidates) => new SubnetSnapshot(number, hash, candidates), rlpBehaviors);
        if (subnetSnapshot is null)
            return null;

        subnetSnapshot.NextEpochPenalties = rlpStream.DecodeArray<Address>(s => s.DecodeAddress()) ?? [];
        return subnetSnapshot;
    }

    protected override void EncodeContent(RlpStream stream, SubnetSnapshot item, RlpBehaviors rlpBehaviors)
    {
        base.EncodeContent(stream, item, rlpBehaviors);

        if (item.NextEpochPenalties is null)
            stream.EncodeArray<Address>([]);
        else
            EncodeAddressSequence(stream, item.NextEpochPenalties);
    }

    protected override int GetContentLength(SubnetSnapshot item, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
            return 0;
        int length = base.GetContentLength(item, rlpBehaviors);
        length += Rlp.LengthOfSequence(Rlp.LengthOfAddressRlp * item.NextEpochPenalties?.Length ?? 0);
        return length;
    }

}
