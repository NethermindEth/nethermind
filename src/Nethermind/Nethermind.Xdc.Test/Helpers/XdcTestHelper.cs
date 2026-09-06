// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.Test.Helpers;

internal static class XdcTestHelper
{
    public static IXdcReleaseSpec CreateXdcReleaseSpec(
        ulong? switchEpoch = null,
        ulong? epochLength = null,
        ulong? switchBlock = null,
        int? maxMasternodes = null,
        double? certThreshold = null,
        int? timeoutPeriod = null,
        ulong? minePeriod = null,
        int? configsCount = null)
    {
        List<V2ConfigParams> v2Configs = [];

        int count = configsCount ?? 1;

        for (int i = 0; i < count; i++)
        {
            v2Configs.Add(new V2ConfigParams
            {
                SwitchRound = 0,
                MaxMasternodes = maxMasternodes ?? 108,
                CertificateThreshold = certThreshold ?? 0.667,
                TimeoutSyncThreshold = 3,
                TimeoutPeriod = timeoutPeriod ?? 30000,
                MinePeriod = minePeriod ?? 2
            });
        }


        XdcReleaseSpec spec = new()
        {
            // Epoch configuration
            SwitchEpoch = switchEpoch ?? 0,
            EpochLength = epochLength ?? 900,
            SwitchBlock = switchBlock ?? 0,
            Gap = 5,

            // V2 Configuration
            MaxMasternodes = maxMasternodes ?? 108,
            MaxProtectorNodes = 0,  // Not used in current implementation
            MaxObserverNodes = 0,   // Not used in current implementation
            SwitchRound = 0,

            // Timing parameters
            MinePeriod = minePeriod ?? 2,              // 2 seconds per block
            TimeoutSyncThreshold = 3,                   // Send sync info after 3 timeouts
            TimeoutPeriod = timeoutPeriod ?? 30000,    // 30 seconds timeout

            // Consensus thresholds
            CertificateThreshold = certThreshold ?? 0.667,     // 2/3 majority for certificates

            // Reward configuration (in Wei)
            Reward = 5000,
            MasternodeReward = 5000,
            ProtectorReward = 0,
            ObserverReward = 0,

            // Penalty configuration
            MinimumMinerBlockPerEpoch = 1,
            LimitPenaltyEpoch = 3,
            MinimumSigningTx = 1,

            // Smart contract addresses (using zero addresses for tests)
            GenesisMasterNodes = Array.Empty<Address>(),
            BlockSignerContract = Address.Zero,
            RandomizeSMCBinary = Address.Zero,
            XDCXLendingFinalizedTradeAddressBinary = Address.Zero,
            XDCXLendingAddressBinary = Address.Zero,
            XDCXAddressBinary = Address.Zero,
            TradingStateAddressBinary = Address.Zero,
            FoundationWallet = Address.Zero,
            MasternodeVotingContract = Address.Zero,

            // Feature flags
            IsBlackListingEnabled = false,
            IsTIP2019 = true,
            IsTIPXDCXMiner = false,

            // Other settings
            MergeSignRange = 15,
            BlackListedAddresses = [],

            // V2 configuration parameters
            V2Configs = v2Configs
        };

        return spec;
    }

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
        KeccakRlpWriter writer = new();
        decoder.Encode(ref writer, new Vote(roundInfo, gapNumber), RlpBehaviors.ForSealing);
        ValueHash256 hash = writer.GetValueHash();
        Signature[] signatures = new Signature[keys.Length];
        Parallel.For(0, keys.Length, i => signatures[i] = ecdsa.Sign(keys[i], hash));
        return signatures;
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
        BlockRoundInfo roundInfo = new(Hash256.Zero, round, round);
        QuorumCertificate qc = CreateQc(roundInfo, gap, [key]);
        Timeout timeout = BuildSignedTimeout(key, round, gap);
        TimeoutCertificate tc = new(round, [timeout.Signature!], gap);
        return new SyncInfo(qc, tc);
    }

    public static Vote BuildSignedVote(BlockRoundInfo info, ulong gap, PrivateKey key)
    {
        Vote vote = new(info, gap);
        KeccakRlpWriter writer = new();
        decoder.Encode(ref writer, vote, RlpBehaviors.ForSealing);
        vote.Signature = ecdsa.Sign(key, writer.GetValueHash());
        vote.Signer = key.Address;
        return vote;
    }

    /// <summary>
    /// Produces a byte-distinct but cryptographically valid alternative signature for the same
    /// message and private key by exploiting secp256k1 malleability: (r, s) → (r, N−s, flipped v).
    /// Both signatures recover to the same signer address, so they represent a single vote from
    /// one validator regardless of how many byte-distinct copies exist.
    /// </summary>
    public static Signature CreateMalleableSignature(Signature original)
    {
        ReadOnlySpan<byte> bytes = original.Bytes; // 64 bytes: r (0..31), s (32..63)

        UInt256 s = new(bytes[32..], true);
        UInt256 sNew = SecP256k1Curve.N - s;

        byte[] result = new byte[65];
        bytes[..32].CopyTo(result); // r unchanged
        sNew.ToBigEndian(result.AsSpan(32, 32));
        result[64] = original.V == 27 ? (byte)28 : (byte)27; // flip recovery id

        return new Signature(result);
    }

    public static byte[] BuildV1ExtraData(Address[] addresses)
    {
        byte[] extraData = new byte[XdcConstants.ExtraVanity + addresses.Length * Address.Size + XdcConstants.ExtraSeal];
        for (int i = 0; i < addresses.Length; i++)
            addresses[i].Bytes.CopyTo(extraData.AsSpan(XdcConstants.ExtraVanity + i * Address.Size, Address.Size));
        return extraData;
    }
}
