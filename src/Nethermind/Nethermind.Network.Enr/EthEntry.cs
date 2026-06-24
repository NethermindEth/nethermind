// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// https://github.com/ethereum/devp2p/blob/master/enr-entries/eth.md
/// </summary>
public class EthEntry(byte[] forkHash, long nextBlock) : EnrContentEntry<ForkId>(new ForkId(forkHash, nextBlock))
{
    public override string Key => EnrContentKey.Eth;

    protected override int GetRlpLengthOfValue()
    {
        int forkIdContentLength = GetForkIdContentLength();
        return Rlp.LengthOfSequence(Rlp.LengthOfSequence(forkIdContentLength));
    }

    protected override void EncodeValue<TWriter>(ref TWriter writer)
    {
        int contentLength = GetForkIdContentLength();
        writer.StartSequence(Rlp.LengthOfSequence(contentLength));
        writer.StartSequence(contentLength);
        writer.Encode(Value.ForkHash);
        writer.Encode(Value.NextBlock);
    }

    private int GetForkIdContentLength() => Rlp.LengthOf(Value.ForkHash) + Rlp.LengthOf(Value.NextBlock);
}
