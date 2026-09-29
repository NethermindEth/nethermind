// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;

namespace Nethermind.Benchmarks.Core;

/// <summary>
/// The EIP-8369 per-IL VERIFY budget fill at its most signature-heavy: all 16 committee positions filled to
/// <c>MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST</c> with the smallest frame transactions that carry one
/// signature, each verifying in full and then failing, so the fill debits only the signature cost.
/// </summary>
/// <remarks>The byte cap binds before the gas budget: at the signature debit alone a position could hold about
/// 370 secp256k1 or 150 P256 entries, but its 8 KiB fits far fewer. Distinct entries at every position is the
/// worst case; one set shared by all positions is what the per-hash verification memo collapses.</remarks>
[MemoryDiagnoser]
public class Eip8369VerifyBudgetFillBenchmarks
{
    private static readonly IReleaseSpec Spec = new Specs.Test.OverridableReleaseSpec(Bogota.Instance) { IsEip8141Enabled = true };
    private readonly EthereumEcdsa _ecdsa = new(BlockchainIds.Mainnet);

    private (Transaction[] Transactions, ushort[] Masks) _distinctSecp256k1;
    private (Transaction[] Transactions, ushort[] Masks) _sharedSecp256k1;
    private (Transaction[] Transactions, ushort[] Masks) _distinctP256;

    [GlobalSetup]
    public void Setup()
    {
        _distinctSecp256k1 = Fill(Secp256k1Signature, distinct: true);
        _sharedSecp256k1 = Fill(Secp256k1Signature, distinct: false);
        _distinctP256 = Fill(P256Signature, distinct: true);
        Console.WriteLine($"// entries: distinct secp256k1 {_distinctSecp256k1.Transactions.Length}, shared secp256k1 {_sharedSecp256k1.Transactions.Length}, distinct P256 {_distinctP256.Transactions.Length}");
    }

    [Benchmark(Baseline = true)]
    public int Secp256k1_distinct_at_every_position() => Run(_distinctSecp256k1);

    [Benchmark]
    public int Secp256k1_shared_by_every_position() => Run(_sharedSecp256k1);

    [Benchmark]
    public int P256_distinct_at_every_position() => Run(_distinctP256);

    private int Run((Transaction[] Transactions, ushort[] Masks) il) =>
        Eip8369Profile2.AdmitByVerifyBudget(InclusionListMembership.ByPosition(il.Transactions, il.Masks), Eip8369Constants.MaxVerifyGasPerTx,
            tx => Profile2EligibilityReplayer.AreSignaturesValid(tx, _ecdsa, Spec)).Count;

    /// <summary>Fills every position to its byte cap, with fresh entries per position or one set for all.</summary>
    private static (Transaction[] Transactions, ushort[] Masks) Fill(Func<ulong, TxFrameSignature> signature, bool distinct)
    {
        List<Transaction> transactions = [];
        List<ushort> masks = [];
        ulong nonce = 0;
        for (int position = 0; position < Eip7805Constants.InclusionListCommitteeSize; position++)
        {
            if (!distinct && position > 0)
            {
                for (int i = 0; i < masks.Count; i++) masks[i] |= (ushort)(1 << position);
                continue;
            }

            int bytes = 0;
            while (true)
            {
                Transaction tx = FrameTx(nonce++, signature);
                byte[] encoded = TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;
                if (bytes + encoded.Length > Eip7805Constants.MaxBytesPerInclusionList) break;
                bytes += encoded.Length;
                tx.Hash = Keccak.Compute(encoded);
                transactions.Add(tx);
                masks.Add((ushort)(1 << position));
            }
        }
        return ([.. transactions], [.. masks]);
    }

    private static Transaction FrameTx(ulong nonce, Func<ulong, TxFrameSignature> signature) => new()
    {
        Type = TxType.FrameTx,
        ChainId = BlockchainIds.Mainnet,
        Nonce = nonce,
        SenderAddress = TestItem.AddressA,
        Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 1, UInt256.Zero, default)],
        FrameSignatures = [signature(nonce)],
        GasPrice = 1,
        DecodedMaxFeePerGas = 1,
    };

    // A canonical signature by another key over another message: recovery runs in full, then the signer mismatches.
    private static TxFrameSignature Secp256k1Signature(ulong seed)
    {
        Signature signed = new EthereumEcdsa(BlockchainIds.Mainnet).Sign(TestItem.PrivateKeyB, Keccak.Compute(seed.ToString()).ValueHash256);
        byte[] raw = new byte[TxFrameSignature.Secp256k1SignatureLength];
        raw[0] = signed.RecoveryId;
        signed.R.Span.CopyTo(raw.AsSpan(1, 32));
        signed.S.Span.CopyTo(raw.AsSpan(33, 32));
        return new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, TestItem.AddressA, ReadOnlyMemory<byte>.Empty, raw);
    }

    // A real key whose address is the signer, with a low-s signature over another message: P256VERIFY runs and fails.
    private static TxFrameSignature P256Signature(ulong seed)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = key.ExportParameters(false);
        byte[] rs = key.SignHash(Keccak.Compute(seed.ToString()).BytesToArray(), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        UInt256 s = new(rs.AsSpan(32, 32), isBigEndian: true);
        if (s > SecP256r1Curve.HalfN) (SecP256r1Curve.N - s).ToBigEndian(rs.AsSpan(32, 32));

        byte[] raw = new byte[TxFrameSignature.P256SignatureLength];
        rs.CopyTo(raw, 0);
        parameters.Q.X!.CopyTo(raw, 64);
        parameters.Q.Y!.CopyTo(raw, 96);
        Address signer = new(Keccak.Compute(raw.AsSpan(64, 64)).Bytes[12..]);
        return new TxFrameSignature(TxFrameSignature.SchemeP256, signer, ReadOnlyMemory<byte>.Empty, raw);
    }
}
