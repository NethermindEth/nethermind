// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

/// <summary>A signed transaction and a call request with its fields and signature, shared by the RPC tests of signed calls.</summary>
internal static class SignedCallScenario
{
    // Content unique to each transaction keeps the tests independent within the process.
    private static long _counter = 100;

    /// <summary>A signed EIP-1559 transaction, the same transaction decoded as it is received, and its signer.</summary>
    /// <param name="nonce">The nonce; a unique one by default.</param>
    /// <param name="unfundedSigner">Signs with <see cref="TestItem.PrivateKeyF"/>, which has no account, with zero fees and value.</param>
    public static (Transaction signed, Transaction received, Address signer) BuildSignedTypedTx(IEthereumEcdsa ecdsa, ulong chainId, ulong? nonce = null, bool unfundedSigner = false)
    {
        PrivateKey signer = unfundedSigner ? TestItem.PrivateKeyF : TestItem.PrivateKeyA;
        ulong unique = (ulong)Interlocked.Increment(ref _counter);
        Transaction signed = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithChainId(chainId)
            .WithNonce(nonce ?? unique)
            .To(TestItem.AddressB)
            .WithMaxFeePerGas(unfundedSigner ? 0 : 100_000_000_000UL)
            .WithMaxPriorityFeePerGas(unfundedSigner ? 0 : 1_000_000_000UL)
            .WithGasLimit(100_000)
            .WithValue(unfundedSigner ? 0 : unique)
            .SignedAndResolved(ecdsa, signer)
            .TestObject;

        return (signed, Decode(signed), signer.Address);
    }

    /// <summary>A call request with the fields and signature of <paramref name="tx"/> and the given <paramref name="from"/>.</summary>
    public static EIP1559TransactionForRpc BuildCall(Transaction tx, Address from, ulong chainId) => new()
    {
        Nonce = tx.Nonce,
        To = tx.To,
        Gas = tx.GasLimit,
        MaxFeePerGas = tx.MaxFeePerGas,
        MaxPriorityFeePerGas = tx.MaxPriorityFeePerGas,
        Value = tx.Value,
        ChainId = chainId,
        From = from,
        R = new UInt256(tx.Signature!.RAsSpan, isBigEndian: true),
        S = new UInt256(tx.Signature!.SAsSpan, isBigEndian: true),
        V = tx.Signature!.RecoveryId == 0 ? UInt256.Zero : UInt256.One,
    };

    public static void AssertRecoversSigner(IEthereumEcdsa ecdsa, Transaction received, Address signer) =>
        Assert.That(ecdsa.RecoverAddress(received), Is.EqualTo(signer), "the transaction recovers its signer");

    /// <summary>The cached sender of <paramref name="tx"/>, or <see langword="null"/>: recovered by an ecdsa that recovers nothing.</summary>
    public static Address? CachedSender(IEthereumEcdsa ecdsa, Transaction tx)
    {
        IEthereumEcdsa nonRecovering = Substitute.For<IEthereumEcdsa>();
        nonRecovering.ChainId.Returns(ecdsa.ChainId);
        return nonRecovering.RecoverAddress(tx);
    }

    private static Transaction Decode(Transaction tx) =>
        Rlp.Decode<Transaction>(TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes, RlpBehaviors.SkipTypedWrapping)!;
}
