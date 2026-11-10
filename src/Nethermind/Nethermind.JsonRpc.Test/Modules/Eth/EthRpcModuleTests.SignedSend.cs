// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
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
using Nethermind.Wallet;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

/// <summary><c>eth_sendTransaction</c> with requests that bring their own signature, signed by B, a funded account.</summary>
public partial class EthRpcModuleTests
{
    // A node that holds no key for the from reports it as unknown, whatever else is wrong with the request.
    [TestCase(true, "from does not match the signer")]
    [TestCase(false, "unknown account")]
    public async Task Signed_request_from_another_account_is_rejected(bool walletHoldsFrom, string expectedError)
    {
        using TestRpcBlockchain test = await SignedSendChain(walletHoldsFrom ? WalletOfBAndC() : NullWallet.Instance);
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

    [Test]
    public async Task Unsigned_request_is_signed_by_the_wallet_for_its_from()
    {
        using TestRpcBlockchain test = await SignedSendChain(WalletOfBAndC());
        LegacyTransactionForRpc request = new()
        {
            From = TestItem.AddressB,
            To = TestItem.AddressD,
            Nonce = 0,
            Gas = GasCostOf.Transaction,
            GasPrice = 20.GWei,
            Value = 1.Wei,
        };

        string response = await test.TestEthRpc("eth_sendTransaction", request);
        Transaction[] pooled = test.TxPool.GetPendingTransactions();

        Assert.That(pooled, Has.Length.EqualTo(1), response);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Is.EqualTo($$"""{"jsonrpc":"2.0","result":"{{pooled[0].Hash}}","id":67}"""));
            Assert.That(pooled[0].SenderAddress, Is.EqualTo(TestItem.AddressB));
            Assert.That(new EthereumEcdsa(test.SpecProvider.ChainId).RecoverAddress(pooled[0]), Is.EqualTo(TestItem.AddressB));
        }
    }

    private static Task<TestRpcBlockchain> SignedSendChain(IWallet wallet) =>
        TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithWallet(wallet).Build(new TestSpecProvider(Prague.Instance));

    /// <summary>A wallet that holds the keys of B and C, with B unlocked.</summary>
    private static DevKeyStoreWallet WalletOfBAndC()
    {
        DevKeyStoreWallet wallet = new(new MemKeyStore([TestItem.PrivateKeyB, TestItem.PrivateKeyC], Path.Combine("testKeyStoreDir", Path.GetRandomFileName())),
            LimboLogs.Instance, createTestAccounts: false);
        wallet.UnlockAccount(TestItem.AddressB, string.Empty.Secure());
        return wallet;
    }

    private static Transaction SignedTransfer(TestRpcBlockchain test, TxType type)
    {
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithType(type)
            .WithChainId(test.SpecProvider.ChainId)
            .WithNonce(0)
            .WithGasLimit(GasCostOf.Transaction)
            .To(TestItem.AddressD)
            .WithValue(1.Wei);
        builder = type == TxType.Legacy ? builder.WithGasPrice(20.GWei) : builder.WithMaxFeePerGas(20.GWei).WithMaxPriorityFeePerGas(1.GWei);
        return builder.SignedAndResolved(new EthereumEcdsa(test.SpecProvider.ChainId), TestItem.PrivateKeyB).TestObject;
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
}
