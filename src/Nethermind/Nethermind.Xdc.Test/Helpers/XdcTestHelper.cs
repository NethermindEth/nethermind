// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Xdc.Test;

internal static class XdcTestHelper
{
    private static readonly EthereumEcdsa ecdsa = new(0);
    private static readonly VoteDecoder decoder = new();

    public static PrivateKey[] GeneratePrivateKeys(int count)
    {
        PrivateKeyGenerator keyBuilder = new();
        return keyBuilder.Generate(count).ToArray();
    }

    public static QuorumCertificate CreateQc(BlockRoundInfo roundInfo, ulong gapNumber, PrivateKey[] keys)
    {
        IEnumerable<Signature> signatures = CreateVoteSignatures(roundInfo, gapNumber, keys);

        return new QuorumCertificate(roundInfo, signatures.ToArray(), gapNumber);
    }

    public static Signature[] CreateVoteSignatures(BlockRoundInfo roundInfo, ulong gapNumber, PrivateKey[] keys)
    {
        VoteDecoder encoder = new();
        IEnumerable<Signature> signatures = keys.Select(k =>
        {
            KeccakRlpStream stream = new();
            encoder.Encode(stream, new Vote(roundInfo, gapNumber), RlpBehaviors.ForSealing);
            return ecdsa.Sign(k, stream.GetValueHash());
        }).ToArray();
        return signatures.ToArray();
    }

    public static Timeout BuildSignedTimeout(PrivateKey key, ulong round, ulong gap)
    {
        TimeoutDecoder decoder = new();
        Timeout timeout = new(round, signature: null, gap);
        Rlp rlp = decoder.Encode(timeout, Nethermind.Serialization.Rlp.RlpBehaviors.ForSealing);
        ValueHash256 hash = Keccak.Compute(rlp.Bytes).ValueHash256;
        Signature signature = new EthereumEcdsa(0).Sign(key, hash);
        return new Timeout(round, signature, gap) { Signer = key.Address };
    }

    public static SyncInfo BuildSyncInfo(PrivateKey key, ulong round, ulong gap)
    {
        BlockRoundInfo roundInfo = new(Hash256.Zero, round, (long)round);
        QuorumCertificate qc = CreateQc(roundInfo, gap, [key]);
        Timeout timeout = BuildSignedTimeout(key, round, gap);
        TimeoutCertificate tc = new(round, [timeout.Signature!], gap);
        return new SyncInfo(qc, tc);
    }

    public static Vote BuildSignedVote(BlockRoundInfo info, ulong gap, PrivateKey key)
    {
        Vote vote = new(info, gap);
        KeccakRlpStream stream = new();
        decoder.Encode(stream, vote, RlpBehaviors.ForSealing);
        vote.Signature = ecdsa.Sign(key, stream.GetValueHash());
        vote.Signer = key.Address;
        return vote;
    }

    public static byte[] BuildV1ExtraData(Address[] addresses)
    {
        byte[] extraData = new byte[XdcConstants.ExtraVanity + addresses.Length * Address.Size + XdcConstants.ExtraSeal];
        for (int i = 0; i < addresses.Length; i++)
            Array.Copy(addresses[i].Bytes, 0, extraData, XdcConstants.ExtraVanity + i * Address.Size, Address.Size);
        return extraData;
    }
}
