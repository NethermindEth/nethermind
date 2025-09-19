// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.RLP;
internal class SnapshotDecoder : IRlpStreamDecoder<Snapshot>
{

    public Snapshot Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
            return null;

        rlpStream.ReadSequenceLength();

        long number = rlpStream.DecodeLong();
        Hash256 hash256 = rlpStream.DecodeKeccak();
        Address[] nextSigners = rlpStream.DecodeArray<Address>(s => s.DecodeAddress()) ?? [];

        return new Snapshot(number, hash256, nextSigners);
    }

    public void Encode(RlpStream stream, Snapshot item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.EncodeNullObject();
            return;
        }

        var contentLength = GetLength(item, rlpBehaviors);

        stream.StartSequence(contentLength);
        stream.Encode(item.Number);
        stream.Encode(item.Hash);

        if(item.NextEpochCandidates is null)
            stream.EncodeArray<Address>([]);
        else
            EncodeNextEpochSigners(stream, item.NextEpochCandidates);
    }

    private void EncodeNextEpochSigners(RlpStream stream, Address[] nextEpochCandidates)
    {
        int length = nextEpochCandidates.Length;
        stream.StartSequence(Rlp.LengthOfAddressRlp * length);
        for (int i = 0; i < length; i++)
        {
            stream.Encode(nextEpochCandidates[i]);
        }
    }

    public int GetLength(Snapshot item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }
    private int GetContentLength(Snapshot item, RlpBehaviors rlpBehaviors)
    {
        if (item is null)
            return 0;

        int length = 0;
        length += Rlp.LengthOf(item.Number);
        length += Rlp.LengthOf(item.Hash);
        length += Rlp.LengthOfSequence(Rlp.LengthOfAddressRlp * item.NextEpochCandidates?.Length ?? 0);
        return length;
    }
}
