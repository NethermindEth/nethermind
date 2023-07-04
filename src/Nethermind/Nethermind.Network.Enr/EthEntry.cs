// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// https://github.com/ethereum/devp2p/blob/master/enr-entries/eth.md
/// </summary>
public class EthEntry : EnrContentEntry<ForkId>
{
    public EthEntry(byte[] forkHash, long nextBlock) : base(new ForkId(forkHash, nextBlock)) { }

    public override string Key => EnrContentKey.Eth;

    protected override int GetRlpLengthOfValue()
    {
        return Rlp.LengthOfSequence(
                Rlp.LengthOfSequence(
                    5 + Rlp.LengthOf(Value.NextBlock)));
    }

    protected override void EncodeValue(RlpStream rlpStream)
    {
        // I am just guessing this one
        int contentLength = 5 + Rlp.LengthOf(Value.NextBlock);
        rlpStream.StartSequence(contentLength + 1);
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(Value.ForkHash);
        rlpStream.Encode(Value.NextBlock);
    }
}
