// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal sealed class SubnetSnapshotDecoder : BaseSnapshotDecoder<SubnetSnapshot>
{

    protected override SubnetSnapshot? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        SubnetSnapshot? subnetSnapshot = DecodeBase<SubnetSnapshot>(ref decoderContext, (number, hash, candidates) => new SubnetSnapshot(number, hash, candidates), rlpBehaviors);
        if (subnetSnapshot is null)
            return null;

        subnetSnapshot.NextEpochPenalties = DecodeAddressArray(ref decoderContext);
        return subnetSnapshot;
    }

    protected override void EncodeContent<TWriter>(ref TWriter writer, SubnetSnapshot item, RlpBehaviors rlpBehaviors)
    {
        base.EncodeContent(ref writer, item, rlpBehaviors);

        if (item.NextEpochPenalties is null)
            writer.StartSequence(0);
        else
            EncodeAddressSequence(ref writer, item.NextEpochPenalties);
    }

    protected override int GetContentLength(SubnetSnapshot? item, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
            return 0;
        int length = base.GetContentLength(item, rlpBehaviors);
        length += Rlp.LengthOfSequence(Rlp.LengthOfAddressRlp * item.NextEpochPenalties?.Length ?? 0);
        return length;
    }

}
