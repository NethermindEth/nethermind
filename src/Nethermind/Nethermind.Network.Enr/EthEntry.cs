// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// https://github.com/ethereum/devp2p/blob/master/enr-entries/eth.md
/// </summary>
public class EthEntry(byte[] forkHash, ulong nextBlock) : EnrContentEntry<ForkId>(new ForkId(forkHash, nextBlock))
{
    public const int ForkHashLength = 4;

    public override string Key => EnrContentKey.Eth;

    protected override int GetRlpLengthOfValue()
    {
        int forkIdContentLength = GetForkIdContentLength();
        return Rlp.LengthOfSequence(Rlp.LengthOfSequence(forkIdContentLength));
    }

    protected override void EncodeValue(RlpStream rlpStream)
    {
        int contentLength = GetForkIdContentLength();
        rlpStream.StartSequence(Rlp.LengthOfSequence(contentLength));
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(Value.ForkHash);
        rlpStream.Encode(Value.NextBlock);
    }

    private int GetForkIdContentLength() => Rlp.LengthOf(Value.ForkHash) + Rlp.LengthOf(Value.NextBlock);
}
