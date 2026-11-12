// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Enr;

/// <summary>
/// https://github.com/ethereum/devp2p/blob/master/enr-entries/eth.md
/// </summary>
public class EthEntry : EnrContentEntry<ForkId>
{
    private readonly byte[]? _originalRlpValue;

    public EthEntry(byte[] forkHash, ulong next)
        : this(forkHash, next, null)
    {
    }

    internal EthEntry(byte[] forkHash, ulong next, byte[]? originalRlpValue)
        : base(new ForkId(forkHash, next))
    {
        if (forkHash.Length != ForkId.ForkHashLength)
        {
            throw new ArgumentException($"Fork hash must be {ForkId.ForkHashLength} bytes.", nameof(forkHash));
        }

        _originalRlpValue = originalRlpValue;
    }

    public override string Key => EnrContentKey.Eth;

    protected override int GetRlpLengthOfValue()
    {
        if (_originalRlpValue is not null)
        {
            return _originalRlpValue.Length;
        }

        int forkIdContentLength = GetForkIdContentLength();
        return Rlp.LengthOfSequence(Rlp.LengthOfSequence(forkIdContentLength));
    }

    protected override void EncodeValue(ref RlpWriter writer)
    {
        if (_originalRlpValue is not null)
        {
            writer.WriteEncoded(_originalRlpValue);
            return;
        }

        int contentLength = GetForkIdContentLength();
        RlpSequence outer = writer.StartSequence(Rlp.LengthOfSequence(contentLength));
        RlpSequence forkId = writer.StartSequence(contentLength);
        writer.Encode(Value.ForkHash);
        writer.Encode(Value.Next);
        writer.EndSequence(forkId);
        writer.EndSequence(outer);
    }

    private int GetForkIdContentLength() => Rlp.LengthOf(Value.ForkHash) + Rlp.LengthOf(Value.Next);
}
