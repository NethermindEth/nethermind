// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal sealed class SubnetSnapshotDecoder : BaseSnapshotDecoder<SubnetSnapshot>
{

    protected override SubnetSnapshot DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        SubnetSnapshot subnetSnapshot = base.DecodeInternal(ref decoderContext, rlpBehaviors);
        subnetSnapshot.NextEpochPenalties = DecodeAddressArray(ref decoderContext);
        return subnetSnapshot;
    }

    protected override SubnetSnapshot DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        SubnetSnapshot subnetSnapshot = base.DecodeInternal(rlpStream, rlpBehaviors);
        subnetSnapshot.NextEpochPenalties = rlpStream.DecodeArray<Address>(s => s.DecodeAddress()) ?? [];
        return subnetSnapshot;
    }

    public override void Encode(RlpStream stream, SubnetSnapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        base.Encode(stream, item, rlpBehaviors);

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

    protected override SubnetSnapshot CreateSnapshot(long number, Hash256 hash, Address[] candidates)
    {
        return new SubnetSnapshot(number, hash, candidates);
    }
}
