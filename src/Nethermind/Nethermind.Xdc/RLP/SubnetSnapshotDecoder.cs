// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal sealed class SubnetSnapshotDecoder : BaseSnapshotDecoder<SubnetSnapshot>
{

    protected override SubnetSnapshot CreateSnapshot(ulong number, Hash256 hash, Address[] candidates) => new(number, hash, candidates);

    protected override void DecodeContent(ref RlpReader fields, SubnetSnapshot snapshot) =>
        snapshot.NextEpochPenalties = XdcRlpHelpers.DecodeAddressArray(ref fields);

    protected override void EncodeContent(ref RlpWriter writer, SubnetSnapshot item, RlpBehaviors rlpBehaviors)
    {
        base.EncodeContent(ref writer, item, rlpBehaviors);

        if (item.NextEpochPenalties is null)
            writer.EncodeNullObject();
        else
            XdcRlpHelpers.EncodeAddressSequence(ref writer, item.NextEpochPenalties);
    }

    protected override int GetContentLength(SubnetSnapshot item, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
            return 0;
        int length = base.GetContentLength(item, rlpBehaviors);
        length += XdcRlpHelpers.LengthOfAddressSequence(item.NextEpochPenalties);
        return length;
    }

}
