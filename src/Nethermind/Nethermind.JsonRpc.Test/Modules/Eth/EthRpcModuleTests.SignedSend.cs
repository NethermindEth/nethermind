// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

/// <summary>
/// <c>eth_sendTransaction</c> with requests that bring their own signature, signed by B, a funded account, unless a test
/// says otherwise. Typed requests give v as 27 or 28.
/// </summary>
public partial class EthRpcModuleTests
{
    // A node that holds no key for the from reports it as unknown, whatever else is wrong with the request.
    [TestCase(true, "from does not match the signer")]
    [TestCase(false, "unknown account")]
    public async Task Signed_request_from_another_account_is_rejected(bool walletHoldsFrom, string expectedError)
    {
        using TestRpcBlockchain test = await SignedSendChain(walletHoldsFrom ? Wallet(holdsC: true) : NullWallet.Instance);
        Transaction signed = SignedTransfer(test, TxType.Legacy);

        string response = await test.TestEthRpc("eth_sendTransaction", SignedRequest(signed, TestItem.AddressC));
        int pooled = test.TxPool.GetPendingTransactionsCount();
        string raw = await test.TestEthRpc("eth_sendRawTransaction", Rlp.Encode(signed, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Is.EqualTo($$"""{"jsonrpc":"2.0","error":{"code":{{ErrorCodes.InvalidInput}},"message":"{{expectedError}}"},"id":67}"""));
            Assert.That(pooled, Is.Zero, "the request left the pool unchanged");
            Assert.That(raw, Is.EqualTo($$"""{"jsonrpc":"2.0","result":"{{signed.Hash}}","id":67}"""), "the transaction is still accepted from its signer");
        }
    }

    [TestCase(TxType.Legacy, false)]
    [TestCase(TxType.Legacy, true)]
    [TestCase(TxType.EIP1559, false)]
    [TestCase(TxType.EIP1559, true)]
    public async Task Signed_request_runs_as_its_signer(TxType type, bool fromGiven)
    {
        using TestRpcBlockchain test = await SignedSendChain(NullWallet.Instance);
        Transaction signed = SignedTransfer(test, type);

        string response = await test.TestEthRpc("eth_sendTransaction", SignedRequest(signed, fromGiven ? TestItem.AddressB : null));
        bool pooled = test.TxPool.TryGetPendingTransaction(signed.Hash!, out Transaction? inPool);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Is.EqualTo($$"""{"jsonrpc":"2.0","result":"{{signed.Hash}}","id":67}"""));
            Assert.That(pooled, Is.True);
            Assert.That(inPool?.SenderAddress, Is.EqualTo(TestItem.AddressB));
        }
    }

    public enum FromField { Omitted, Signer, AddressRecoveredWithoutTheSignatureChainId }

    /// <remarks>
    /// Where the spec does not validate chain ids, a legacy signature made for another chain is accepted, and block
    /// processing recovers its signer over the chain id the signature carries. The address a recovery without that chain
    /// id gives is funded, so a transaction sent as it would be pooled too.
    /// </remarks>
    [TestCase(FromField.Omitted, true)]
    [TestCase(FromField.Signer, true)]
    [TestCase(FromField.AddressRecoveredWithoutTheSignatureChainId, false)]
    public async Task Signed_request_for_another_chain_runs_as_its_signer_where_chain_ids_are_not_validated(FromField from, bool accepted)
    {
        using TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithWallet(NullWallet.Instance)
            .Build(new TestSpecProvider(new OverridableReleaseSpec(Prague.Instance) { ValidateChainId = false }));
        ulong chainId = test.SpecProvider.ChainId;
        Transaction signed = SignedTransfer(test, TxType.Legacy, nonce: 1, signatureChainId: chainId + 1);
        Address otherAddress = new EthereumEcdsa(chainId).RecoverAddress(signed)!;
        await test.AddBlock(Build.A.Transaction.WithNonce(0).WithGasPrice(20.GWei).WithGasLimit(GasCostOf.Transaction).To(otherAddress)
            .WithValue(1.Ether).SignedAndResolved(new EthereumEcdsa(chainId), TestItem.PrivateKeyB).TestObject);
        Address? fromAddress = from switch
        {
            FromField.Signer => TestItem.AddressB,
            FromField.AddressRecoveredWithoutTheSignatureChainId => otherAddress,
            _ => null
        };

        string response = await test.TestEthRpc("eth_sendTransaction", SignedRequest(signed, fromAddress));
        Address?[] pooledSenders = [.. test.TxPool.GetPendingTransactions().Select(static tx => tx.SenderAddress)];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(otherAddress, Is.Not.EqualTo(TestItem.AddressB), "precondition: without its chain id the signature recovers another address");
            Assert.That(test.ReadOnlyState.GetBalance(otherAddress).IsZero, Is.False, "precondition: that address is funded");
            Assert.That(response, Is.EqualTo(accepted
                ? $$"""{"jsonrpc":"2.0","result":"{{signed.Hash}}","id":67}"""
                : $$"""{"jsonrpc":"2.0","error":{"code":{{ErrorCodes.InvalidInput}},"message":"unknown account"},"id":67}"""));
            Assert.That(pooledSenders, Is.EqualTo(accepted ? new[] { TestItem.AddressB } : Array.Empty<Address>()), "the senders in the pool");
        }
    }

    [Test]
    public async Task Unsigned_request_is_signed_by_the_wallet_for_its_from()
    {
        using TestRpcBlockchain test = await SignedSendChain(Wallet(holdsC: true));

        string response = await test.TestEthRpc("eth_sendTransaction", UnsignedRequest(nonce: 0));
        Transaction[] pooled = test.TxPool.GetPendingTransactions();

        Assert.That(pooled, Has.Length.EqualTo(1), response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Is.EqualTo($$"""{"jsonrpc":"2.0","result":"{{pooled[0].Hash}}","id":67}"""));
            Assert.That(pooled[0].SenderAddress, Is.EqualTo(TestItem.AddressB));
            Assert.That(new EthereumEcdsa(test.SpecProvider.ChainId).RecoverAddress(pooled[0]), Is.EqualTo(TestItem.AddressB));
        }
    }

    /// <remarks>
    /// B has a transaction pending at nonce 0, so a request without a nonce gets nonce 1, and the wallet signs it for its
    /// from whatever signature the request brought: over nonce 0, over the nonce it gets, or by another account.
    /// </remarks>
    [TestCase(TxType.Legacy, true, 0UL)]
    [TestCase(TxType.Legacy, true, 1UL)]
    [TestCase(TxType.Legacy, false, 0UL)]
    [TestCase(TxType.EIP1559, true, 1UL)]
    public async Task Signed_request_without_nonce_is_signed_by_the_wallet_for_its_from(TxType type, bool signedByFrom, ulong signedNonce)
    {
        using TestRpcBlockchain test = await SignedSendChain(Wallet(holdsC: true));
        string pending = await test.TestEthRpc("eth_sendTransaction", UnsignedRequest(nonce: 0));
        LegacyTransactionForRpc request = SignedRequest(
            SignedTransfer(test, type, signedByFrom ? TestItem.PrivateKeyB : TestItem.PrivateKeyC, signedNonce), TestItem.AddressB);
        request.Nonce = null;

        string response = await test.TestEthRpc("eth_sendTransaction", request);
        Transaction? sent = test.TxPool.GetPendingTransactions().SingleOrDefault(static tx => tx.Nonce == 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pending, Does.Contain("\"result\""), "precondition: B has a transaction pending at nonce 0");
            Assert.That(response, Is.EqualTo($$"""{"jsonrpc":"2.0","result":"{{sent?.Hash}}","id":67}"""));
            Assert.That(sent?.SenderAddress, Is.EqualTo(TestItem.AddressB));
            Assert.That(sent is null ? null : new EthereumEcdsa(test.SpecProvider.ChainId).RecoverAddress(sent), Is.EqualTo(TestItem.AddressB),
                "the wallet signed it for B");
        }
    }

    public enum UnsignableFrom { Locked, NotHeld, Omitted }

    /// <remarks>
    /// The request brings a signature its sender's next nonce matches, so keeping it would have pooled the transaction.
    /// With from left out there is no account for the wallet to sign for.
    /// </remarks>
    [TestCase(UnsignableFrom.Locked, ErrorCodes.AccountLocked, "authentication needed: password or unlock")]
    [TestCase(UnsignableFrom.NotHeld, ErrorCodes.InvalidInput, "unknown account")]
    [TestCase(UnsignableFrom.Omitted, ErrorCodes.InvalidInput, "unknown account")]
    public async Task Signed_request_without_nonce_fails_when_the_wallet_cannot_sign_for_its_from(UnsignableFrom from, int code, string message)
    {
        using TestRpcBlockchain test = await SignedSendChain(Wallet(holdsC: from == UnsignableFrom.Locked));
        LegacyTransactionForRpc request = from == UnsignableFrom.Omitted
            ? SignedRequest(SignedTransfer(test, TxType.Legacy), null)
            : SignedRequest(SignedTransfer(test, TxType.Legacy, TestItem.PrivateKeyC), TestItem.AddressC);
        request.Nonce = null;

        string response = await test.TestEthRpc("eth_sendTransaction", request);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Is.EqualTo($$"""{"jsonrpc":"2.0","error":{"code":{{code}},"message":"{{message}}"},"id":67}"""));
            Assert.That(test.TxPool.GetPendingTransactionsCount(), Is.Zero, "the request left the pool unchanged");
        }
    }

    [Test]
    public async Task Signed_request_without_nonce_is_sent_without_its_signature()
    {
        ITxSender txSender = Substitute.For<ITxSender>();
        Transaction? sent = null;
        txSender.SendTransaction(Arg.Do<Transaction>(tx => sent = tx), Arg.Any<TxHandlingOptions>()).Returns((TestItem.KeccakA, AcceptTxResult.Accepted));
        using TestRpcBlockchain test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithWallet(Wallet(holdsC: true))
            .WithTxSender(txSender).Build(new TestSpecProvider(Prague.Instance));
        LegacyTransactionForRpc request = SignedRequest(SignedTransfer(test, TxType.Legacy, TestItem.PrivateKeyC), TestItem.AddressB);
        request.Nonce = null;

        await test.TestEthRpc("eth_sendTransaction", request);

        Assert.That(sent, Is.Not.Null, "precondition: the transaction was sent");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent!.Signature, Is.Null);
            Assert.That(sent.SenderAddress, Is.EqualTo(TestItem.AddressB));
        }

        await txSender.Received(1).SendTransaction(Arg.Any<Transaction>(), TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast);
    }

    private static Task<TestRpcBlockchain> SignedSendChain(IWallet wallet) =>
        TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithWallet(wallet).Build(new TestSpecProvider(Prague.Instance));

    /// <summary>A wallet that holds the key of B, unlocked, and with <paramref name="holdsC"/> the key of C, locked.</summary>
    private static DevKeyStoreWallet Wallet(bool holdsC)
    {
        PrivateKey[] keys = holdsC ? [TestItem.PrivateKeyB, TestItem.PrivateKeyC] : [TestItem.PrivateKeyB];
        DevKeyStoreWallet wallet = new(new MemKeyStore(keys, Path.Combine("testKeyStoreDir", Path.GetRandomFileName())),
            LimboLogs.Instance, createTestAccounts: false);
        wallet.UnlockAccount(TestItem.AddressB, string.Empty.Secure());
        return wallet;
    }

    /// <summary>A transfer of 1 wei to D, signed by <paramref name="signer"/> (B by default).</summary>
    /// <param name="signatureChainId">The chain id a legacy signature is made for, the node's by default.</param>
    private static Transaction SignedTransfer(TestRpcBlockchain test, TxType type, PrivateKey? signer = null, ulong nonce = 0, ulong? signatureChainId = null)
    {
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithType(type)
            .WithChainId(test.SpecProvider.ChainId)
            .WithNonce(nonce)
            .WithGasLimit(GasCostOf.Transaction)
            .To(TestItem.AddressD)
            .WithValue(1.Wei);
        builder = type == TxType.Legacy ? builder.WithGasPrice(20.GWei) : builder.WithMaxFeePerGas(20.GWei).WithMaxPriorityFeePerGas(1.GWei);
        return builder.SignedAndResolved(new EthereumEcdsa(signatureChainId ?? test.SpecProvider.ChainId), signer ?? TestItem.PrivateKeyB).TestObject;
    }

    /// <summary>The request that brings the fields and the signature of <paramref name="signed"/>, sent with <paramref name="from"/>.</summary>
    private static LegacyTransactionForRpc SignedRequest(Transaction signed, Address? from)
    {
        LegacyTransactionForRpc request = (LegacyTransactionForRpc)TransactionForRpc.FromTransaction(signed);
        request.From = from;
        if (request is EIP1559TransactionForRpc typed)
        {
            // A typed request is priced by its fee fields alone, and its v is read as 27 or 28.
            typed.GasPrice = null;
            typed.V = (UInt256)(27UL + signed.Signature!.RecoveryId);
        }

        return request;
    }

    /// <summary>A transfer of 1 wei from B to D for the wallet to sign.</summary>
    private static LegacyTransactionForRpc UnsignedRequest(ulong? nonce) => new()
    {
        From = TestItem.AddressB,
        To = TestItem.AddressD,
        Nonce = nonce,
        Gas = GasCostOf.Transaction,
        GasPrice = 20.GWei,
        Value = 1.Wei,
    };
}
