// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
internal static class XdcTestHelper
{
    private static readonly EthereumEcdsa ecdsa = new EthereumEcdsa(0);
    private static readonly VoteDecoder decoder = new VoteDecoder();

    public static PrivateKey[] GeneratePrivateKeys(int count)
    {
        var keyBuilder = new PrivateKeyGenerator();
        return keyBuilder.Generate(count).ToArray();
    }

    public static QuorumCertificate CreateQc(BlockRoundInfo roundInfo, ulong gapNumber, PrivateKey[] keys)
    {
        var qcEncoder = new VoteDecoder();

        IEnumerable<Signature> signatures = CreateVoteSignatures(roundInfo, gapNumber, keys);

        return new QuorumCertificate(roundInfo, signatures.ToArray(), gapNumber);
    }

    public static Signature[] CreateVoteSignatures(BlockRoundInfo roundInfo, ulong gapnumber, PrivateKey[] keys)
    {
        var encoder = new VoteDecoder();
        IEnumerable<Signature> signatures = keys.Select(k =>
        {
            var stream = new KeccakRlpStream();
            encoder.Encode(stream, new Vote(roundInfo, gapnumber), RlpBehaviors.ForSealing);
            return ecdsa.Sign(k, stream.GetValueHash());
        }).ToArray();
        return signatures.ToArray();
    }

    public static Vote BuildSignedVote(
    BlockRoundInfo info, ulong gap, PrivateKey key)
    {
        var vote = new Vote(info, gap);
        var stream = new KeccakRlpStream();
        decoder.Encode(stream, vote, RlpBehaviors.ForSealing);
        vote.Signature = ecdsa.Sign(key, stream.GetValueHash());
        vote.Signer = key.Address;
        return vote;
    }
}
