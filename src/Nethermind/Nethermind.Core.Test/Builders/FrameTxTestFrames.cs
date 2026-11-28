// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core.Test.Builders;

/// <summary>Builders for the EIP-8141 frame layouts the frame transaction fixtures are exercised against.</summary>
/// <remarks>Shared so a change to the frame grammar lands in one place rather than in every fixture.</remarks>
public static class FrameTxTestFrames
{
    /// <summary>A validation-prefix gas limit in the range a real prefix uses.</summary>
    public const ulong PrefixFrameGas = 100_000;

    public static Transaction FrameTx(params TxFrame[] frames) => FrameTx(TestItem.AddressA, [], frames);

    public static Transaction FrameTx(Address sender, TxFrameClaim[] claims, params TxFrame[] frames) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = sender,
        Frames = frames,
        FrameClaims = claims,
    };

    /// <summary>A SECP256K1 claim over the canonical signature hash, keyed to <paramref name="signer"/> or, for
    /// <see langword="null"/>, to the sender.</summary>
    public static TxFrameClaim Secp256k1Claim(Address? signer) =>
        new(Eip8141Constants.Secp256k1VerifierAddress, signer is null ? default : signer.Bytes.ToArray(), default, 0, 0);

    /// <summary>Installs a single SECP256K1 claim over <paramref name="tx"/>'s canonical signature hash, with its witness.</summary>
    /// <remarks>The signature hash covers the claim but not the witness, so the claim is installed before signing.</remarks>
    public static void SignSecp256k1(Transaction tx, PrivateKey key, Address? signer)
    {
        tx.FrameClaims = [Secp256k1Claim(signer)];
        tx.ClaimWitnesses = [Secp256k1SignatureBytes(new Ecdsa().Sign(key, FrameTxSigHash.ComputeValue(tx)))];
    }

    /// <summary>A signature in the EIP-8141 <c>v || r || s</c> encoding, <c>v</c> being the 0/1 recovery id.</summary>
    public static byte[] Secp256k1SignatureBytes(Signature signature)
    {
        byte[] bytes = new byte[Eip8141Constants.Secp256k1WitnessLength];
        bytes[0] = signature.RecoveryId;
        signature.Bytes.CopyTo(bytes.AsSpan(1));
        return bytes;
    }

    /// <summary>A chain of SECP256K1 claims that all require curve recovery, the last one failing verification.</summary>
    /// <remarks>The admission measurements need the worst case: no claim short-circuits before the mismatch,
    /// so the whole chain is recovered before the transaction is rejected.</remarks>
    public static (TxFrameClaim[] Claims, byte[][] Witnesses) RecoveredSecp256k1Claims(EthereumEcdsa ecdsa, int count)
    {
        TxFrameClaim[] claims = new TxFrameClaim[count];
        byte[][] witnesses = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            byte[] msg = ValueKeccak.Compute(BitConverter.GetBytes(i)).ToByteArray();
            byte[] signed = i == count - 1 ? ValueKeccak.Compute("mismatch"u8).ToByteArray() : msg;
            claims[i] = new TxFrameClaim(Eip8141Constants.Secp256k1VerifierAddress, TestItem.PrivateKeyA.Address.Bytes.ToArray(), msg, 0, 0);
            witnesses[i] = Secp256k1SignatureBytes(ecdsa.Sign(TestItem.PrivateKeyA, new Hash256(signed)));
        }

        return (claims, witnesses);
    }

    /// <summary>A SECP256K1 claim by <paramref name="key"/> over an explicit digest, with its witness, so claims can be
    /// built independently of each other and of the transaction's signature hash.</summary>
    public static (TxFrameClaim Claim, byte[] Witness) DigestClaim(PrivateKey key, byte fill)
    {
        byte[] digest = new byte[Hash256.Size];
        digest[31] = fill;
        byte[] witness = Secp256k1SignatureBytes(new Ecdsa().Sign(key, new ValueHash256(digest)));
        return (new TxFrameClaim(Eip8141Constants.Secp256k1VerifierAddress, key.Address.Bytes.ToArray(), digest, 0, 0), witness);
    }

    public static TxFrame SelfVerify(ulong gasLimit = 1_000) =>
        new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit, UInt256.Zero, default);

    public static TxFrame OnlyVerify(ulong gasLimit = 1_000) =>
        new(FrameMode.Verify, FrameFlags.ApproveExecution, target: null, gasLimit, UInt256.Zero, default);

    public static TxFrame Pay(ulong gasLimit = 1_000) => Pay(TestItem.AddressC, gasLimit);

    /// <remarks>A null <paramref name="target"/> is the omitted-target encoding, not a missing argument.</remarks>
    public static TxFrame Pay(Address? target, ulong gasLimit = 1_000) =>
        new(FrameMode.Verify, FrameFlags.ApprovePayment, target, gasLimit, UInt256.Zero, default);

    public static TxFrame Deploy(ulong gasLimit = 1_000) =>
        new(FrameMode.Default, FrameFlags.None, TestItem.AddressD, gasLimit, UInt256.Zero, default);

    public static TxFrame Execution(ulong gasLimit = 1_000) =>
        new(FrameMode.Sender, FrameFlags.None, TestItem.AddressB, gasLimit, UInt256.Zero, default);

    public static TxFrame AssertFrame(ulong gasLimit = 1_000) =>
        new(FrameMode.Assert, FrameFlags.None, TestItem.AddressB, gasLimit, UInt256.Zero, default);

    /// <summary>A VERIFY frame carrying no approval scope: a validation step ahead of the approving frame.</summary>
    /// <remarks>The prefix grammar names no shape starting with one, so a layout leading with it is unrecognized.</remarks>
    public static TxFrame ExtraVerify(ulong gasLimit = 1_000) =>
        new(FrameMode.Verify, FrameFlags.None, TestItem.AddressC, gasLimit, UInt256.Zero, default);

    /// <remarks>Approving flags on a DEFAULT frame make the layout unrecognized rather than ending the prefix.</remarks>
    public static TxFrame ApprovingDefault(ulong gasLimit = 1_000) =>
        new(FrameMode.Default, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit, UInt256.Zero, default);

    public static TxFrame Expiry(ulong gasLimit = 30_000) => ExpiryAt(deadline: 0, gasLimit);

    /// <summary>An EIP-8272 <c>recent_root_verify</c> frame carrying <paramref name="tuples"/> as packed 72-byte data.</summary>
    public static TxFrame RecentRootVerify(ulong gasLimit, params (ValueHash256 SourceId, ulong Slot, ValueHash256 Root)[] tuples) =>
        new(FrameMode.Verify, FrameFlags.None, Eip8272Constants.RecentRootAddress, gasLimit, UInt256.Zero, RecentRootTuples(tuples));

    public static byte[] RecentRootTuples(params (ValueHash256 SourceId, ulong Slot, ValueHash256 Root)[] tuples)
    {
        const int tupleBytes = Eip8272Constants.RecentRootTupleLength;
        byte[] data = new byte[tuples.Length * tupleBytes];
        for (int i = 0; i < tuples.Length; i++)
        {
            Span<byte> tuple = data.AsSpan(i * tupleBytes, tupleBytes);
            tuples[i].SourceId.Bytes.CopyTo(tuple);
            BinaryPrimitives.WriteUInt64BigEndian(tuple.Slice(32, sizeof(ulong)), tuples[i].Slot);
            tuples[i].Root.Bytes.CopyTo(tuple.Slice(40));
        }

        return data;
    }

    public static TxFrame ExpiryAt(ulong deadline, ulong gasLimit = 30_000)
    {
        byte[] data = new byte[Eip8141Constants.ExpiryDataLength];
        BinaryPrimitives.WriteUInt64BigEndian(data, deadline);
        return new TxFrame(FrameMode.Verify, FrameFlags.None, Eip8141Constants.ExpiryVerifierAddress, gasLimit, UInt256.Zero, data);
    }

    /// <summary>A leanSPHINCS claim of <paramref name="publicKey"/> over <paramref name="message"/>.</summary>
    public static TxFrameClaim LeanSphincsClaim(Hash256 publicKey, Hash256 message) =>
        new(Eip8141Constants.LeanSphincsVerifierAddress, publicKey.BytesToArray(), message.BytesToArray(), 0, 0);

    /// <summary>A hashed frame transaction with a leanSPHINCS claim per key and message, each witnessed by a placeholder
    /// signature, and optionally a SECP256K1 claim with a placeholder witness after them.</summary>
    public static Transaction LeanSphincsTx(ulong nonce, bool withSecp256k1Claim, params (Hash256 Key, Hash256 Message)[] claims)
    {
        TxFrameClaim[] frameClaims = new TxFrameClaim[claims.Length + (withSecp256k1Claim ? 1 : 0)];
        byte[][] witnesses = new byte[frameClaims.Length][];
        for (int i = 0; i < claims.Length; i++)
        {
            frameClaims[i] = LeanSphincsClaim(claims[i].Key, claims[i].Message);
            witnesses[i] = new byte[Eip8141Constants.LeanSphincsSignatureSize];
            Array.Fill(witnesses[i], (byte)(i + 1));
        }

        if (withSecp256k1Claim)
        {
            frameClaims[^1] = Secp256k1Claim(null);
            witnesses[^1] = new byte[Eip8141Constants.Secp256k1WitnessLength];
            Array.Fill(witnesses[^1], (byte)0xee);
        }

        Transaction tx = FrameTx(TestItem.AddressA, frameClaims, SelfVerify());
        tx.ChainId = TestBlockchainIds.ChainId;
        tx.Nonce = nonce;
        tx.ClaimWitnesses = witnesses;
        tx.Hash = tx.CalculateHash();
        return tx;
    }
}
