// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.Test;

/// <summary>Fixtures shared by the QBFT tests: addresses by number, deterministic seals, BFT-shaped headers.</summary>
public static class QbftTestData
{
    public static Address Addr(ulong number) => Address.FromNumber(number);

    /// <summary>Besu's <c>createSignature(r, s, recId)</c>: a syntactically valid seal that does not recover to anyone in particular.</summary>
    public static Signature Seal(ulong r, ulong s, byte recoveryId) => new((UInt256)r, (UInt256)s, Signature.VOffset + (ulong)recoveryId);

    /// <summary>Bytes 1..32, Besu's <c>createNonEmptyVanityData()</c>.</summary>
    public static byte[] NonEmptyVanity()
    {
        byte[] vanity = new byte[BftExtraData.VanityLength];
        for (int i = 0; i < vanity.Length; i++) vanity[i] = (byte)(i + 1);
        return vanity;
    }

    public static byte[] ZeroVanity() => new byte[BftExtraData.VanityLength];

    public static BlockHeaderBuilder BftHeader(ulong number, Address proposer, byte[] extraData) => Build.A.BlockHeader
        .WithNumber(number)
        .WithBeneficiary(proposer)
        .WithDifficulty(UInt256.One)
        .WithMixHash(BftHelpers.ExpectedMixHash)
        .WithNonce(0)
        .WithExtraData(extraData);

    /// <summary>A header typed as <see cref="BftBlockHeader"/> whose hash is the BFT on-chain digest.</summary>
    public static BftBlockHeader ToQbftHeader(BlockHeader header, IBftExtraDataCodec? codec = null)
    {
        BftBlockHeader qbft = BftBlockHeader.UpgradeFrom(header, codec ?? QbftExtraDataCodec.Instance);
        qbft.Hash = new Hash256(qbft.CalculateHash());
        return qbft;
    }

    public static List<PrivateKey> Keys(int count)
    {
        List<PrivateKey> keys = [];
        PrivateKeyGenerator generator = new();
        for (int i = 0; i < count; i++) keys.Add(generator.Generate());
        return keys;
    }
}

/// <summary>Signed-message helpers: one <see cref="MessageFactory"/> per validator key over the QBFT-only codec.</summary>
public static class QbftTestMessages
{
    public static readonly BftBlockInterface BlockInterface = new(QbftOnlyCodecSelector.Instance);
    public static readonly QbftMessageCodec Codec = new();

    public static MessageFactory Factory(PrivateKey key, bool legacy = false) =>
        new(new Signer(1, key, LimboLogs.Instance), Codec, BlockInterface, legacy);

    /// <summary>An unsealed round-zero proposal block at <paramref name="number"/> with the given validator set.</summary>
    public static Block Block(ulong number, Address proposer, IReadOnlyList<Address> validators, int round = 0, ulong timestamp = 1000)
    {
        byte[] extraData = QbftExtraDataCodec.Instance.Encode(new BftExtraData(QbftTestData.ZeroVanity(), [], null, round, validators));
        BlockHeader header = QbftTestData.BftHeader(number, proposer, extraData).WithTimestamp(timestamp).WithParentHash(Keccak.Compute(number.ToString())).TestObject;
        return Build.A.Block.WithHeader(QbftTestData.ToQbftHeader(header)).TestObject;
    }

    public static Hash256 Digest(Block block) => new(BlockInterface.Digest(block));

    public static Address[] Addresses(IReadOnlyList<PrivateKey> keys)
    {
        Address[] result = new Address[keys.Count];
        for (int i = 0; i < keys.Count; i++) result[i] = keys[i].Address;
        return result;
    }
}
