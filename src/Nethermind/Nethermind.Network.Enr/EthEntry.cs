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

    protected override void EncodeValue<TBackend>(ref ValueRlpWriter<TBackend> writer)
    {
        // I am just guessing this one
        int contentLength = 5 + Rlp.LengthOf(Value.NextBlock);
        writer.StartSequence(contentLength + 1);
        writer.StartSequence(contentLength);
        writer.Encode(Value.ForkHash);
        writer.Encode(Value.NextBlock);
    }
}
