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

    protected override int GetRlpLengthOfValue() => Rlp.LengthOfSequence(
                Rlp.LengthOfSequence(
                    5 + Rlp.LengthOf(Value.NextBlock)));

    protected override void EncodeValue(RlpStream rlpStream)
    {
        // I am just guessing this one
        int contentLength = 5 + Rlp.LengthOf(Value.NextBlock);
        rlpStream.StartSequence(contentLength + 1);
        rlpStream.StartSequence(contentLength);
        rlpStream.Encode(Value.ForkHash);
        rlpStream.Encode(Value.NextBlock);
    }

    protected override void EncodeValue(Span<byte> buffer, ref int position)
    {
        // I am just guessing this one
        int contentLength = 5 + Rlp.LengthOf(Value.NextBlock);
        position = Rlp.StartSequence(buffer, position, contentLength + 1);
        position = Rlp.StartSequence(buffer, position, contentLength);
        position = Rlp.Encode(buffer, position, Value.ForkHash);
        position += Rlp.Encode((ulong)Value.NextBlock, buffer[position..]).Length;
    }
}
