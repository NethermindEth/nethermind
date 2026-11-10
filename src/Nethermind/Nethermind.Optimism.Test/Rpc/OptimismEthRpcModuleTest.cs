// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.History;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NSubstitute.Extensions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.Rpc;

public class OptimismEthRpcModuleTest
{
    [SetUp]
    public void Setup()
    {
        TransactionForRpc.RegisterTransactionType<DepositTransactionForRpc>();
        TxDecoder.Instance.RegisterDecoder(new OptimismTxDecoder());
        TxDecoder.Instance.RegisterDecoder(new LegacyTxDecoder(allowEmptySignature: true));
    }

    private static IBlockFinder MockBlockFinder(Block block)
    {
        IBlockFinder bf = Substitute.For<IBlockFinder>();
        bf.FindBlock(new BlockParameter(block.Hash!)).Returns(block);
        bf.FindBlock(new BlockParameter(block.Number)).Returns(block);
        return bf;
    }

    private static IReceiptFinder MockReceiptFinder(Block block, params TxReceipt[] receipts)
    {
        IReceiptFinder rf = Substitute.For<IReceiptFinder>();
        rf.Get(block).Returns(receipts);
        return rf;
    }

    private static Task<TestRpcBlockchain> BuildOptimismRpc(IBlockFinder blockFinder, IReceiptFinder receiptFinder, ISpecProvider? specProvider = null) =>
        TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithBlockFinder(blockFinder)
            .WithReceiptFinder(receiptFinder)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: Substitute.For<IJsonRpcClient>(),
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: Substitute.For<IEthereumEcdsa>(),
                sealer: Substitute.For<ITxSealer>(),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build(specProvider);

    private sealed class NoRecoveryEthereumEcdsa(IEthereumEcdsa signer) : IEthereumEcdsa
    {
        public ulong ChainId => signer.ChainId;

        public Signature Sign(PrivateKey privateKey, in ValueHash256 message) => signer.Sign(privateKey, in message);

        public PublicKey? RecoverPublicKey(Signature signature, in ValueHash256 message) => signer.RecoverPublicKey(signature, in message);

        public CompressedPublicKey? RecoverCompressedPublicKey(Signature signature, in ValueHash256 message) =>
            signer.RecoverCompressedPublicKey(signature, in message);

        public Address? RecoverAddress(Signature signature, in ValueHash256 message) => null;
    }

    private sealed class NullSenderTransactionForRpc(Transaction transaction) : LegacyTransactionForRpc
    {
        public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null)
        {
            Transaction tx = new();
            transaction.CopyTo(tx);
            tx.SenderAddress = null;
            return tx;
        }

        public override bool ShouldSetBaseFee() => false;
    }

    [Test]
    public async Task Sequencer_send_transaction_with_signature_will_not_try_to_sign()
    {
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        ITxSender txSender = Substitute.For<ITxSender>();
        txSender.SendTransaction(tx: Arg.Any<Transaction>(), txHandlingOptions: TxHandlingOptions.PersistentBroadcast)
            .Returns(returnThis: (TestItem.KeccakA, AcceptTxResult.Accepted));

        EthereumEcdsa ethereumEcdsa = new(chainId: TestBlockchainIds.ChainId);
        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithBlockchainBridge(bridge)
            .WithTxSender(txSender)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: null /* explicitly using null to behave as Sequencer */,
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: new OptimismEthereumEcdsa(ethereumEcdsa),
                sealer: Substitute.For<ITxSealer>(),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();

        Transaction tx = Build.A.Transaction
            .Signed(ecdsa: ethereumEcdsa, privateKey: TestItem.PrivateKeyA)
            .TestObject;
        string serialized = await rpcBlockchain.TestEthRpc("eth_sendRawTransaction", Rlp.Encode(item: tx, behaviors: RlpBehaviors.None).Bytes.ToHexString());

        await txSender.Received().SendTransaction(tx: Arg.Any<Transaction>(), txHandlingOptions: TxHandlingOptions.PersistentBroadcast);
        Assert.That(serialized, Is.EqualTo($$"""{"jsonrpc":"2.0","result":"{{TestItem.KeccakA.Bytes.ToHexString(withZeroX: true)}}","id":67}"""));
    }

    private const string ForwardedHash = "0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760";
    private const string FailedToForward = """{"jsonrpc":"2.0","error":{"code":-32603,"message":"Failed to forward transaction"},"id":67}""";

    [TestCase("""{"jsonrpc":"2.0","id":67,"error":{"code":-32000,"message":"nonce too low"}}""",
        """{"jsonrpc":"2.0","error":{"code":-32000,"message":"nonce too low"},"id":67}""",
        TestName = "Forwarded transaction rejected by the sequencer returns its error")]
    [TestCase($$"""{"jsonrpc":"2.0","id":67,"result":"{{ForwardedHash}}"}""",
        $$"""{"jsonrpc":"2.0","result":"{{ForwardedHash}}","id":67}""",
        TestName = "Forwarded transaction accepted by the sequencer returns its hash")]
    [TestCase("", FailedToForward, TestName = "Empty sequencer response returns failed to forward")]
    [TestCase("<html>502 Bad Gateway</html>", FailedToForward, TestName = "Non-JSON sequencer response returns failed to forward")]
    public async Task Send_raw_transaction_forwards_the_sequencer_response(string sequencerResponse, string expected)
    {
        IJsonRpcClient sequencer = Substitute.For<IJsonRpcClient>();
        sequencer.Post("eth_sendRawTransaction", Arg.Any<object?[]>()).Returns(sequencerResponse);

        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: sequencer,
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: Substitute.For<IEthereumEcdsa>(),
                sealer: Substitute.For<ITxSealer>(),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();

        Transaction tx = Build.A.Transaction.Signed(TestItem.PrivateKeyA).TestObject;
        string serialized = await rpcBlockchain.TestEthRpc("eth_sendRawTransaction", Rlp.Encode(item: tx, behaviors: RlpBehaviors.None).Bytes.ToHexString());

        Assert.That(serialized, Is.EqualTo(expected));
    }

    [Test]
    public async Task Send_transaction_without_a_sender_it_can_recover_reports_an_unknown_account()
    {
        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        ITxSealer sealer = Substitute.For<ITxSealer>();
        EthereumEcdsa signingEcdsa = new(chainId: TestBlockchainIds.ChainId);
        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithBlockchainBridge(bridge)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: null /* explicitly using null to behave as Sequencer */,
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: new NoRecoveryEthereumEcdsa(signingEcdsa),
                sealer: sealer,
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();

        Transaction tx = Build.A.Transaction
            .Signed(ecdsa: signingEcdsa, privateKey: TestItem.PrivateKeyA)
            .TestObject;
        SignableTransactionForRpc rpcTx = new NullSenderTransactionForRpc(tx);

        ResultWrapper<Hash256> result = await rpcBlockchain.EthRpcModule.eth_sendTransaction(rpcTx);

        sealer.DidNotReceive().TrySeal(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidInput), "a request with no sender comes from no account this node holds");
            Assert.That(result.Result.Error, Is.EqualTo("unknown account"), "a request with no sender comes from no account this node holds");
        }
    }

    [TestCase("0x7e5f4552091a69125d5dfcb7b8c2659029395bdf", "maxFeePerGas (0xa) < maxPriorityFeePerGas (0x3b9aca00)", TestName = "Send_transaction_from_a_held_account_reports_the_fee_rule_it_breaks")]
    [TestCase("0x000000000000000000000000000000000000dead", "unknown account", TestName = "Send_transaction_from_an_account_the_node_does_not_hold_reports_an_unknown_account")]
    public async Task Send_transaction_checks_fees_and_account_like_the_base_module(string from, string expected)
    {
        ITxSealer sealer = Substitute.For<ITxSealer>();
        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: null,
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: Substitute.For<IEthereumEcdsa>(),
                sealer: sealer,
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();
        object? request = JsonSerializer.Deserialize<object>(
            $$"""{"type":"0x2","from":"{{from}}","to":"0x2d44c0e097f6cd0f514edac633d82e01280b4a5c","gas":"0x76c0","nonce":"0x0","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x3b9aca00"}""");

        string serialized = await rpcBlockchain.TestEthRpc("eth_sendTransaction", request);

        sealer.DidNotReceive().TrySeal(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo(expected),
            "the request is checked as the base module checks it, before it is signed and forwarded");
    }

    [TestCase("""{"type":"0x2","maxFeePerGas":"0xa","maxPriorityFeePerGas":"0x3b9aca00"}""", "maxFeePerGas (0xa) < maxPriorityFeePerGas (0x3b9aca00)", TestName = "Send_transaction_from_a_key_the_account_list_misses_reports_the_fee_rule_it_breaks")]
    [TestCase("""{"gasPrice":"0x9184e72a000"}""", "authentication needed: password or unlock", TestName = "Send_transaction_from_a_locked_key_the_account_list_misses_needs_authentication")]
    public async Task Send_transaction_from_a_key_the_account_list_misses_keeps_the_real_error(string feeFields, string expected)
    {
        Address unlisted = TestItem.AddressD;
        IWallet wallet = Substitute.For<IWallet>();
        wallet.GetAccounts().Returns([]);
        wallet.HasKey(unlisted).Returns(true);
        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithWallet(wallet)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: null,
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: Substitute.For<IEthereumEcdsa>(),
                sealer: Substitute.For<ITxSealer>(),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();
        JsonObject fields = JsonNode.Parse(feeFields)!.AsObject();
        fields["from"] = unlisted.ToString();
        fields["to"] = "0x2d44c0e097f6cd0f514edac633d82e01280b4a5c";
        fields["gas"] = "0x76c0";
        fields["nonce"] = "0x0";
        object? request = JsonSerializer.Deserialize<object>(fields.ToJsonString());

        string serialized = await rpcBlockchain.TestEthRpc("eth_sendTransaction", request);

        Assert.That(JToken.Parse(serialized)["error"]?["message"]?.Value<string>(), Is.EqualTo(expected),
            "the wallet holds a key for the account, so its real error must not become an unknown account");
    }

    [Test]
    public async Task Send_transaction_without_nonce_gas_and_fees_fills_them([Values] bool forwardToSequencer)
    {
        UInt256 suggestedFee = 1_000_000_000;
        IGasPriceOracle oracle = Substitute.For<IGasPriceOracle>();
        oracle.GetMaxPriorityGasFeeEstimate().Returns(suggestedFee);
        ITxSealer sealer = Substitute.For<ITxSealer>();
        sealer.TrySeal(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(true);
        IJsonRpcClient sequencer = Substitute.For<IJsonRpcClient>();
        Transaction? sent = null;
        sequencer.Post("eth_sendRawTransaction", Arg.Do<object?[]>(parameters =>
            sent = TxDecoder.Instance.DecodeCompleteNotNull((byte[])parameters[0]!, RlpBehaviors.AllowUnsigned | RlpBehaviors.SkipTypedWrapping)))
            .Returns($$"""{"jsonrpc":"2.0","id":67,"result":"{{ForwardedHash}}"}""");
        ITxSender txSender = Substitute.For<ITxSender>();
        txSender.SendTransaction(Arg.Do<Transaction>(tx => sent = tx), Arg.Any<TxHandlingOptions>())
            .Returns((TestItem.KeccakA, AcceptTxResult.Accepted));
        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithGasPriceOracle(oracle)
            .WithTxSender(txSender)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: forwardToSequencer ? sequencer : null,
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: Substitute.For<IEthereumEcdsa>(),
                sealer: sealer,
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build(new TestSpecProvider(London.Instance));
        await rpcBlockchain.AddBlock(Build.A.Transaction.WithNonce(0).WithTo(TestItem.AddressA)
            .WithMaxFeePerGas(20.GWei).WithMaxPriorityFeePerGas(1.GWei).WithType(TxType.EIP1559)
            .SignedAndResolved(TestItem.PrivateKeyC).TestObject);
        object? request = JsonSerializer.Deserialize<object>(
            $$"""{"from":"{{TestItem.AddressC}}","to":"0x2d44c0e097f6cd0f514edac633d82e01280b4a5c","value":"0x1"}""");

        string serialized = await rpcBlockchain.TestEthRpc("eth_sendTransaction", request);

        Assert.That(JToken.Parse(serialized)["error"], Is.Null, serialized);
        BlockHeader head = rpcBlockchain.BlockTree.Head!.Header;
        UInt256 nextBaseFee = BaseFeeCalculator.Calculate(head, rpcBlockchain.SpecProvider.GetSpecFor1559(head.Number + 1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sent!.Nonce, Is.EqualTo(1UL), "the nonce must follow the account's mined transaction");
            Assert.That(sent.GasLimit, Is.EqualTo((ulong)GasCostOf.Transaction), "gas must be estimated");
            Assert.That(sent.MaxPriorityFeePerGas, Is.EqualTo(suggestedFee), "the tip must come from the gas price oracle");
            Assert.That(sent.MaxFeePerGas, Is.EqualTo(2 * nextBaseFee + suggestedFee), "the fee cap must be twice the next base fee plus the tip");
        }
    }

    private const string AcceptedByTheSequencer = $$"""{"jsonrpc":"2.0","id":67,"result":"{{ForwardedHash}}"}""";

    /// <summary>
    /// A sequencer that keeps every transaction forwarded to it and answers with <paramref name="respond"/>, called
    /// with the index of the transaction, or accepts it.
    /// </summary>
    private sealed class RecordingSequencer(Func<int, Task<string?>>? respond = null) : IJsonRpcClient
    {
        private readonly List<Transaction> _received = [];

        public Transaction[] Received
        {
            get
            {
                lock (_received)
                    return _received.ToArray();
            }
        }

        public ulong[] ReceivedNonces => Received.Select(static tx => tx.Nonce).ToArray();

        public async Task<string?> Post(string method, params object?[] parameters)
        {
            Transaction tx = TxDecoder.Instance.DecodeCompleteNotNull((byte[])parameters[0]!, RlpBehaviors.AllowUnsigned | RlpBehaviors.SkipTypedWrapping);
            int index;
            lock (_received)
            {
                index = _received.Count;
                _received.Add(tx);
            }

            return respond is null ? AcceptedByTheSequencer : await respond(index);
        }

        public Task<T?> Post<T>(string method, params object?[] parameters) => throw new NotSupportedException();
    }

    /// <summary>
    /// Releases two sends from <paramref name="sender"/> that the test holds at the pool or the sequencer: the first, which
    /// is held there when this is called, and then the second. The timeouts only keep a broken implementation from hanging
    /// the test.
    /// </summary>
    /// <returns>
    /// Whether the account's lock counted the second send as a user while the first still held the lock, before the second
    /// reached the pool or the sequencer; and whether the second held the lock when it got there, after the first was done.
    /// </returns>
    internal static async Task<(bool Waited, bool HeldTheLock)> ReleaseTwoSendsInTurn(ForwardedNonceTracker nonceTracker,
        Address sender, TaskCompletionSource releaseFirst, Task first, Task secondArrived, TaskCompletionSource releaseSecond)
    {
        bool countedWhileFirstHeld = SpinWait.SpinUntil(
            () => nonceTracker.SenderLockState(sender) == (2, true) || secondArrived.IsCompleted, TimeSpan.FromSeconds(10));
        bool waited = countedWhileFirstHeld && !secondArrived.IsCompleted;
        releaseFirst.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(30));
        await secondArrived.WaitAsync(TimeSpan.FromSeconds(30));
        bool heldTheLock = nonceTracker.SenderLockState(sender) == (1, true);
        releaseSecond.SetResult();
        return (waited, heldTheLock);
    }

    private static async Task<TestRpcBlockchain> BuildSendRpc(IJsonRpcClient? sequencer, ITxSender? txSender = null, HeadState? headState = null,
        ReorgingHead? reorgingHead = null)
    {
        TestSpecProvider specProvider = new(London.Instance);
        TestRpcBlockchain.Builder<TestRpcBlockchain> builder = TestRpcBlockchain.ForTest(sealEngineType: SealEngineType.Optimism);
        if (txSender is not null)
            builder = builder.WithTxSender(txSender);
        if (reorgingHead is not null)
            builder = builder.WithBlockFinder(reorgingHead.Finder);

        TestRpcBlockchain rpcBlockchain = await builder
            .WithOptimismEthRpcModule(
                sequencerRpcClient: sequencer,
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: Substitute.For<IEthereumEcdsa>(),
                sealer: new TxSealer(new WalletTxSigner(WalletWithKeysOfABAndC(), specProvider.ChainId), Timestamper.Default),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>(),
                nonceStateReader: headState?.Reader ?? reorgingHead?.Reader)
            .Build(specProvider);
        if (headState is not null)
            headState.Chain = rpcBlockchain;

        return rpcBlockchain;
    }

    // The node's wallet: it signs for A, B and C, and for no other account.
    private static IWallet WalletWithKeysOfABAndC()
    {
        PrivateKey[] keys = [TestItem.PrivateKeyA, TestItem.PrivateKeyB, TestItem.PrivateKeyC];
        IWallet wallet = Substitute.For<IWallet>();
        wallet.TrySignTransaction(Arg.Any<Transaction>(), Arg.Any<ulong>()).Returns(call =>
        {
            Transaction tx = call.Arg<Transaction>();
            if (keys.FirstOrDefault(key => key.Address == tx.SenderAddress) is not { } key)
                return false;

            new EthereumEcdsa(call.ArgAt<ulong>(1)).Sign(key, tx);
            return true;
        });
        return wallet;
    }

    public enum StateFailure { MissingTrieNode, CorruptTrieNode, SnapshotNotGathered, StoreClosed, StateNotRetained }

    /// <summary>
    /// The chain's state as the nonce tracker reads it, which can fail: on a node that has not synced or has pruned the
    /// head state, holds a corrupt trie node, cannot gather its flat state snapshot in time or is closing its database.
    /// </summary>
    private sealed class HeadState
    {
        private int _holdNextRead;

        public HeadState() =>
            Reader.TryGetAccount(Arg.Any<BlockHeader?>(), Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(call =>
            {
                IStateReader reader = Failure switch
                {
                    null => Chain!.StateReader,
                    StateFailure.MissingTrieNode => throw new MissingTrieNodeException("The head state is not available", null, TreePath.Empty, Keccak.EmptyTreeHash),
                    // The real reader, over a trie whose nodes do not decode.
                    StateFailure.CorruptTrieNode => CorruptTrieReader(),
                    // As FlatDbManager gives up gathering a snapshot.
                    StateFailure.SnapshotNotGathered => throw new InvalidOperationException("Timed out gathering ReadOnlySnapshotBundle"),
                    StateFailure.StoreClosed => throw new ObjectDisposedException("state db"),
                    // As a pruned node reports state it no longer keeps.
                    _ => throw new MissingTrieNodeException("The head state is not retained", null, TreePath.Empty, Keccak.EmptyTreeHash,
                        new StateNotRetainedException("The head state is pruned")),
                };
                bool exists = reader.TryGetAccount(call.ArgAt<BlockHeader?>(0), call.ArgAt<Address>(1), out AccountStruct account);
                call[2] = account;
                // A held read keeps what it read at the head of its time, while the chain moves on, until it is released.
                if (Interlocked.Exchange(ref _holdNextRead, 0) == 1)
                {
                    ReadHeld.TrySetResult();
                    ReleaseRead.Task.Wait(TimeSpan.FromSeconds(30));
                }

                return exists;
            });

        public IStateReader Reader { get; } = Substitute.For<IStateReader>();

        public TestRpcBlockchain? Chain { get; set; }

        // Null while the state can be read.
        public StateFailure? Failure { get; set; }

        private static StateReader CorruptTrieReader()
        {
            IScopedTrieStore nodes = Substitute.For<IScopedTrieStore>();
            nodes.FindCachedOrUnknown(Arg.Any<TreePath>(), Arg.Any<Hash256>()).Returns(call => new TrieNode(NodeType.Unknown, call.Arg<Hash256>(), new byte[] { 0xc0 }));
            ITrieStore trieStore = Substitute.For<ITrieStore>();
            trieStore.GetTrieStore(null).Returns(nodes);
            return new StateReader(trieStore, Substitute.For<IKeyValueStore>(), LimboLogs.Instance);
        }

        /// <summary>The next read sets <see cref="ReadHeld"/> once it has read the state, then waits for <see cref="ReleaseRead"/>.</summary>
        public void HoldNextRead() => Volatile.Write(ref _holdNextRead, 1);

        public TaskCompletionSource ReadHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static Transaction SignedByC(TestRpcBlockchain rpcBlockchain, ulong nonce) =>
        Build.A.Transaction.WithNonce(nonce).WithTo(TestItem.AddressA)
            .WithMaxFeePerGas(20.GWei).WithMaxPriorityFeePerGas(1.GWei).WithType(TxType.EIP1559)
            .WithChainId(rpcBlockchain.SpecProvider.ChainId)
            .SignedAndResolved(TestItem.PrivateKeyC).TestObject;

    private static Task<string> SendFromC(TestRpcBlockchain rpcBlockchain, string extraFields = "") =>
        SendFrom(rpcBlockchain, TestItem.AddressC, extraFields);

    // A send left waiting on a lock fails the test instead of hanging it.
    private static Task<string> SendFrom(TestRpcBlockchain rpcBlockchain, Address from, string extraFields = "") =>
        rpcBlockchain.TestEthRpc("eth_sendTransaction", JsonSerializer.Deserialize<object>(
            $$"""{"from":"{{from}}","to":"0x2d44c0e097f6cd0f514edac633d82e01280b4a5c","value":"0x1"{{extraFields}}}"""))
            .WaitAsync(TimeSpan.FromSeconds(30));

    private static void AssertSucceeded(string serialized) => Assert.That(JToken.Parse(serialized)["error"], Is.Null, serialized);

    [Test]
    public async Task Forwarding_node_fills_consecutive_nonces_before_the_first_is_included()
    {
        RecordingSequencer sequencer = new();
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));

        AssertSucceeded(await SendFromC(rpcBlockchain));
        AssertSucceeded(await SendFromC(rpcBlockchain));

        Assert.That(sequencer.ReceivedNonces, Is.EqualTo(new ulong[] { 1, 2 }),
            "the second send must follow the first, which the head does not include yet");
    }

    [Test]
    public async Task Forwarding_node_sends_from_another_account_while_a_forward_is_held()
    {
        TaskCompletionSource firstArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingSequencer sequencer = new(async index =>
        {
            if (index == 0)
            {
                firstArrived.SetResult();
                await releaseFirst.Task;
            }

            return AcceptedByTheSequencer;
        });
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));

        Task<string> fromC = SendFromC(rpcBlockchain);
        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<string> fromB = SendFrom(rpcBlockchain, TestItem.AddressB);
        // The forward of C stays at the sequencer until B is done or has waited long enough to show it is blocked.
        bool bDoneWhileCHeld = await Task.WhenAny(fromB, Task.Delay(TimeSpan.FromSeconds(5))) == fromB;
        releaseFirst.SetResult();
        AssertSucceeded(await fromC);
        AssertSucceeded(await fromB);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bDoneWhileCHeld, Is.True, "a forward held for one account must not hold the sends of another");
            Assert.That(sequencer.ReceivedNonces, Is.EqualTo(new ulong[] { 1, 0 }), "C follows its mined transaction, B starts at 0");
        }
    }

    [Test]
    public async Task Forwarding_node_follows_the_head_once_it_includes_the_forwarded_transactions()
    {
        RecordingSequencer sequencer = new();
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer);

        AssertSucceeded(await SendFromC(rpcBlockchain));
        AssertSucceeded(await SendFromC(rpcBlockchain));
        // The head includes nonces 0 to 2 of the account, one more than this node forwarded.
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0), SignedByC(rpcBlockchain, 1), SignedByC(rpcBlockchain, 2));
        AssertSucceeded(await SendFromC(rpcBlockchain));

        Assert.That(sequencer.ReceivedNonces, Is.EqualTo(new ulong[] { 0, 1, 3 }));
    }

    public enum ForwardFailure { Rejected, NoAnswer, NetworkError }

    [TestCase(ForwardFailure.Rejected, TestName = "Forwarding_node_does_not_count_a_transaction_the_sequencer_rejects")]
    [TestCase(ForwardFailure.NoAnswer, TestName = "Forwarding_node_does_not_count_a_transaction_the_sequencer_does_not_answer")]
    [TestCase(ForwardFailure.NetworkError, TestName = "Forwarding_node_does_not_count_a_transaction_lost_to_a_network_error")]
    public async Task Forwarding_node_does_not_count_a_failed_forward(ForwardFailure failure)
    {
        RecordingSequencer sequencer = new(index => index > 0
            ? Task.FromResult<string?>(AcceptedByTheSequencer)
            : failure switch
            {
                ForwardFailure.Rejected => Task.FromResult<string?>("""{"jsonrpc":"2.0","id":67,"error":{"code":-32000,"message":"insufficient funds for gas * price + value"}}"""),
                ForwardFailure.NoAnswer => Task.FromResult<string?>(""),
                _ => Task.FromException<string?>(new HttpRequestException("connection refused")),
            });
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));

        string failed = await SendFromC(rpcBlockchain);
        AssertSucceeded(await SendFromC(rpcBlockchain));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(failed)["error"], Is.Not.Null, failed);
            Assert.That(sequencer.ReceivedNonces, Is.EqualTo(new ulong[] { 1, 1 }), "a failed forward must not use up its nonce");
        }
    }

    [Test]
    public async Task Send_transaction_keeps_an_explicit_nonce([Values] bool forwardToSequencer)
    {
        RecordingSequencer sequencer = new();
        Transaction? sentToPool = null;
        ITxSender txSender = Substitute.For<ITxSender>();
        txSender.SendTransaction(Arg.Do<Transaction>(tx => sentToPool = tx), Arg.Any<TxHandlingOptions>())
            .Returns((TestItem.KeccakA, AcceptTxResult.Accepted));
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(forwardToSequencer ? sequencer : null, txSender);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));

        AssertSucceeded(await SendFromC(rpcBlockchain, ",\"nonce\":\"0x5\""));

        ulong sent = forwardToSequencer ? sequencer.ReceivedNonces.Single() : sentToPool!.Nonce;
        Assert.That(sent, Is.EqualTo(5UL), "a nonce given in the request must be sent as it is");
    }

    [TestCase(0, 5UL, new ulong[] { 5, 1 }, TestName = "Forwarding_node_does_not_fill_past_an_explicit_nonce_after_a_gap")]
    [TestCase(0, 1UL, new ulong[] { 1, 2 }, TestName = "Forwarding_node_fills_past_an_explicit_nonce_without_a_gap")]
    // Nonces 1 and 2 are with the sequencer, which accepts a replacement of 1.
    [TestCase(2, 1UL, new ulong[] { 1, 2, 1, 3 }, TestName = "Forwarding_node_does_not_go_back_after_replacing_a_nonce_below_the_next")]
    public async Task Forwarding_node_counts_an_explicit_nonce_without_a_gap(int filledBefore, ulong explicitNonce, ulong[] sent)
    {
        RecordingSequencer sequencer = new();
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));

        for (int i = 0; i < filledBefore; i++)
            AssertSucceeded(await SendFromC(rpcBlockchain));
        AssertSucceeded(await SendFromC(rpcBlockchain, $",\"nonce\":\"0x{explicitNonce:x}\""));
        AssertSucceeded(await SendFromC(rpcBlockchain));

        Assert.That(sequencer.ReceivedNonces, Is.EqualTo(sent));
    }

    public enum NotSignedByNode { SignedByAnotherAccount, Frame }

    [TestCase(NotSignedByNode.SignedByAnotherAccount, TestName = "Forwarding_node_does_not_count_a_transaction_signed_by_another_account")]
    [TestCase(NotSignedByNode.Frame, TestName = "Forwarding_node_does_not_count_a_frame_transaction_it_leaves_unsigned")]
    public async Task Forwarding_node_does_not_count_a_transaction_it_did_not_sign(NotSignedByNode kind)
    {
        RecordingSequencer sequencer = new();
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer);
        SignableTransactionForRpc request = kind switch
        {
            // Signed by A and sent with C as its from: the node forwards the signature it was given, so C has sent nothing.
            NotSignedByNode.SignedByAnotherAccount => new LegacyTransactionForRpc(Build.A.Transaction.WithNonce(0).WithTo(TestItem.AddressB)
                .WithGasPrice(1.GWei).WithChainId(rpcBlockchain.SpecProvider.ChainId).SignedAndResolved(TestItem.PrivateKeyA).TestObject, default)
            { From = TestItem.AddressC },
            // A frame transaction has no outer signature, so the sealer leaves it unsigned and still succeeds.
            _ => new FrameTransactionForRpc
            {
                From = TestItem.AddressC,
                Gas = 50_000,
                MaxFee = 1.GWei,
                MaxPriorityFeePerGas = 1,
                Frames = [new FrameForRpc { Target = TestItem.AddressB, ExecutionGas = 50_000, StateGas = 0 }],
                Signatures = [],
            },
        };

        AssertSucceeded(await rpcBlockchain.TestEthRpc("eth_sendTransaction", request).WaitAsync(TimeSpan.FromSeconds(30)));
        AssertSucceeded(await SendFromC(rpcBlockchain));

        Assert.That(sequencer.ReceivedNonces, Is.EqualTo(new ulong[] { 0, 0 }), "a transaction the node did not sign must not use up a nonce of its from");
    }

    [TestCase(StateFailure.MissingTrieNode, ErrorCodes.ResourceNotFound)]
    [TestCase(StateFailure.CorruptTrieNode, ErrorCodes.InternalError)]
    [TestCase(StateFailure.SnapshotNotGathered, ErrorCodes.InternalError)]
    [TestCase(StateFailure.StoreClosed, ErrorCodes.InternalError)]
    public async Task Forwarding_node_forwards_an_explicit_nonce_without_the_head_state(StateFailure failure, int errorCode)
    {
        RecordingSequencer sequencer = new();
        HeadState headState = new() { Failure = failure };
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer, headState: headState);

        string withoutNonce = await SendFromC(rpcBlockchain);
        AssertSucceeded(await SendFromC(rpcBlockchain, ",\"nonce\":\"0x5\""));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(withoutNonce)["error"]?["code"]?.Value<int>(), Is.EqualTo(errorCode),
                "without the head state there is no nonce to fill, so the state error stops the send");
            Assert.That(sequencer.ReceivedNonces, Is.EqualTo(new ulong[] { 5 }), "a request with its nonce needs no head state");
        }
    }

    [Test]
    public async Task Forwarding_node_counts_an_accepted_forward_after_the_head_state_goes()
    {
        HeadState headState = new();
        // The head state goes while the first forward is at the sequencer, which accepts it.
        RecordingSequencer sequencer = new(index =>
        {
            if (index == 0)
                headState.Failure = StateFailure.MissingTrieNode;

            return Task.FromResult<string?>(AcceptedByTheSequencer);
        });
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer, headState: headState);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));

        string accepted = await SendFromC(rpcBlockchain);
        headState.Failure = null;
        AssertSucceeded(await SendFromC(rpcBlockchain));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JToken.Parse(accepted)["error"], Is.Null, "a forward the sequencer accepted must be answered as accepted");
            Assert.That(sequencer.ReceivedNonces, Is.EqualTo(new ulong[] { 1, 2 }), "the accepted forward must use up its nonce");
        }
    }

    [Test]
    public async Task Sequencer_node_fills_the_nonce_after_a_transaction_waiting_in_the_pool()
    {
        Transaction? sent = null;
        ITxSender txSender = Substitute.For<ITxSender>();
        txSender.SendTransaction(Arg.Do<Transaction>(tx => sent = tx), Arg.Any<TxHandlingOptions>())
            .Returns((TestItem.KeccakA, AcceptTxResult.Accepted));
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer: null, txSender);
        Assert.That(rpcBlockchain.TxPool.SubmitTx(SignedByC(rpcBlockchain, 0), TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted),
            "the first transaction must wait in the pool");

        AssertSucceeded(await SendFromC(rpcBlockchain));

        Assert.That(sent!.Nonce, Is.EqualTo(1UL), "the nonce must follow the account's transaction in the pool, not the head");
    }

    [Test]
    public async Task Concurrent_sends_from_one_account_get_consecutive_nonces([Values] bool forwardToSequencer)
    {
        TaskCompletionSource firstArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<ulong> sentToPool = [];
        TestRpcBlockchain? rpcBlockchain = null;

        // Each send is held at the sequencer, or before it reaches the pool, until the test releases it.
        async Task Hold(int index)
        {
            if (index > 0)
            {
                secondArrived.TrySetResult();
                await releaseSecond.Task;
                return;
            }

            firstArrived.SetResult();
            await releaseFirst.Task;
        }

        async Task<(Hash256 Hash, AcceptTxResult? AddTxResult)> AddToPool(Transaction tx)
        {
            int index;
            lock (sentToPool)
            {
                index = sentToPool.Count;
                sentToPool.Add(tx.Nonce);
            }

            await Hold(index);
            return (TestItem.KeccakA, rpcBlockchain!.TxPool.SubmitTx(SignedByC(rpcBlockchain, tx.Nonce), TxHandlingOptions.None));
        }

        RecordingSequencer sequencer = new(async index =>
        {
            await Hold(index);
            return AcceptedByTheSequencer;
        });
        ITxSender txSender = Substitute.For<ITxSender>();
        txSender.SendTransaction(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(call =>
            new ValueTask<(Hash256 Hash, AcceptTxResult? AddTxResult)>(AddToPool(call.Arg<Transaction>())));

        rpcBlockchain = await BuildSendRpc(forwardToSequencer ? sequencer : null, txSender);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));

        Task<string> first = SendFromC(rpcBlockchain);
        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<string> second = SendFromC(rpcBlockchain);
        // Unless the sends are serialised, the second one is filled and sent while the first is still on its way.
        (bool secondWaited, bool secondHeldTheLock) = await ReleaseTwoSendsInTurn(((OptimismEthRpcModule)rpcBlockchain.EthRpcModule).NonceTracker,
            TestItem.AddressC, releaseFirst, first, secondArrived.Task, releaseSecond);
        AssertSucceeded(await first);
        AssertSucceeded(await second);

        ulong[] sent;
        lock (sentToPool)
            sent = forwardToSequencer ? sequencer.ReceivedNonces : sentToPool.ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondWaited, Is.True, "the second send must wait for the first");
            Assert.That(secondHeldTheLock, Is.True, "the second send must hold the lock when it is sent");
            Assert.That(sent, Is.EqualTo(new ulong[] { 1, 2 }), "concurrent sends must not share a nonce");
        }
    }

    public enum SecondSend { ExplicitNonce, OwnSignatureWithoutNonce }

    // A send that already has its nonce or its signature still waits: the nonce it is compared with, or the one filled for
    // it, must follow the send before it.
    [TestCase(SecondSend.ExplicitNonce, new ulong[] { 0, 1, 2 }, TestName = "Forwarding_node_holds_a_send_with_an_explicit_nonce_until_the_previous_one_is_done")]
    // Signed by C for nonce 1, with the nonce left out of the request. A signature the request brings is not counted, so
    // the next filled send repeats its nonce.
    [TestCase(SecondSend.OwnSignatureWithoutNonce, new ulong[] { 0, 1, 1 }, TestName = "Forwarding_node_holds_a_send_with_its_own_signature_until_the_previous_one_is_done")]
    public async Task Forwarding_node_holds_a_send_until_the_previous_one_is_done(SecondSend kind, ulong[] sent)
    {
        TaskCompletionSource firstArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // The first two forwards are held at the sequencer until the test releases them.
        RecordingSequencer sequencer = new(async index =>
        {
            if (index == 0)
            {
                firstArrived.SetResult();
                await releaseFirst.Task;
            }
            else if (index == 1)
            {
                secondArrived.SetResult();
                await releaseSecond.Task;
            }

            return AcceptedByTheSequencer;
        });
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer);

        Transaction signedByC = Build.A.Transaction.WithNonce(1).WithTo(TestItem.AddressB).WithGasPrice(1.GWei)
            .WithChainId(rpcBlockchain.SpecProvider.ChainId).SignedAndResolved(TestItem.PrivateKeyC).TestObject;

        Task<string> first = SendFromC(rpcBlockchain);
        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<string> second = kind == SecondSend.ExplicitNonce
            ? SendFromC(rpcBlockchain, ",\"nonce\":\"0x1\"")
            : rpcBlockchain.TestEthRpc("eth_sendTransaction", new LegacyTransactionForRpc(signedByC, default) { From = TestItem.AddressC, Nonce = null })
                .WaitAsync(TimeSpan.FromSeconds(30));
        (bool secondWaited, bool secondHeldTheLock) = await ReleaseTwoSendsInTurn(((OptimismEthRpcModule)rpcBlockchain.EthRpcModule).NonceTracker,
            TestItem.AddressC, releaseFirst, first, secondArrived.Task, releaseSecond);
        AssertSucceeded(await first);
        AssertSucceeded(await second);
        AssertSucceeded(await SendFromC(rpcBlockchain));

        EthereumEcdsa ecdsa = new(rpcBlockchain.SpecProvider.ChainId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondWaited, Is.True, "the second send must wait for the first");
            Assert.That(secondHeldTheLock, Is.True, "the second send must hold the lock when it is sent");
            Assert.That(sequencer.ReceivedNonces, Is.EqualTo(sent));
            Assert.That(sequencer.Received.Select(tx => ecdsa.RecoverAddress(tx)), Is.All.EqualTo(TestItem.AddressC),
                "every forward must carry a signature of C over what is sent");
        }
    }

    private static async Task<JToken> TransactionCountOfC(TestRpcBlockchain rpcBlockchain, params string[] blockParameter) =>
        JToken.Parse(await rpcBlockchain.TestEthRpc("eth_getTransactionCount", [TestItem.AddressC.ToString(), .. blockParameter]));

    private static async Task<JToken> FillFromC(TestRpcBlockchain rpcBlockchain, string extraFields = "") =>
        JToken.Parse(await rpcBlockchain.TestEthRpc("eth_fillTransaction", JsonSerializer.Deserialize<object>(
            $$"""{"from":"{{TestItem.AddressC}}","to":"0x2d44c0e097f6cd0f514edac633d82e01280b4a5c","value":"0x1"{{extraFields}}}""")));

    private static async Task<JToken> FillWithoutFrom(TestRpcBlockchain rpcBlockchain) =>
        JToken.Parse(await rpcBlockchain.TestEthRpc("eth_fillTransaction", JsonSerializer.Deserialize<object>(
            """{"to":"0x2d44c0e097f6cd0f514edac633d82e01280b4a5c","gas":"0x5208"}""")));

    private static string? FilledNonce(JToken filled) => (string?)filled["result"]?["tx"]?["nonce"];

    [Test]
    public async Task Forwarding_node_reports_the_head_nonce_as_pending()
    {
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(new RecordingSequencer());
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));

        JToken pending = await TransactionCountOfC(rpcBlockchain, "pending");

        Assert.That((string?)pending["result"], Is.EqualTo("0x1"), pending.ToString());
    }

    [Test]
    public async Task Forwarding_node_fills_the_head_nonce()
    {
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(new RecordingSequencer());
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));

        JToken filled = await FillFromC(rpcBlockchain);

        Assert.That(FilledNonce(filled), Is.EqualTo("0x1"), filled.ToString());
    }

    [Test]
    public async Task Forwarding_node_reports_and_fills_the_nonce_the_next_send_fills()
    {
        RecordingSequencer sequencer = new();
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));

        AssertSucceeded(await SendFromC(rpcBlockchain));
        JToken pending = await TransactionCountOfC(rpcBlockchain, "pending");
        JToken filled = await FillFromC(rpcBlockchain);
        AssertSucceeded(await SendFromC(rpcBlockchain));

        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)pending["result"], Is.EqualTo("0x2"), "the sequencer accepted nonce 1, which the head does not include yet");
            Assert.That(FilledNonce(filled), Is.EqualTo("0x2"), filled.ToString());
            Assert.That(sequencer.ReceivedNonces, Is.EqualTo(new ulong[] { 1, 2 }), "the next send fills the nonce reported as pending");
        }
    }

    [Test]
    public async Task Fill_transaction_keeps_an_explicit_nonce([Values] bool forwardToSequencer)
    {
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(forwardToSequencer ? new RecordingSequencer() : null);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));

        JToken filled = await FillFromC(rpcBlockchain, ",\"nonce\":\"0x5\"");

        Assert.That(FilledNonce(filled), Is.EqualTo("0x5"), filled.ToString());
    }

    [Test]
    public async Task Sequencer_node_reports_and_fills_the_nonce_after_a_transaction_waiting_in_the_pool()
    {
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer: null);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));
        Assert.That(rpcBlockchain.TxPool.SubmitTx(SignedByC(rpcBlockchain, 1), TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted),
            "the second transaction must wait in the pool");

        JToken pending = await TransactionCountOfC(rpcBlockchain, "pending");
        JToken filled = await FillFromC(rpcBlockchain);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)pending["result"], Is.EqualTo("0x2"), "the pending nonce must follow the transaction in the pool, not the head");
            Assert.That(FilledNonce(filled), Is.EqualTo("0x2"), filled.ToString());
        }
    }

    [Test]
    public async Task Transaction_count_at_a_block_leaves_out_what_the_head_does_not_include([Values] bool forwardToSequencer)
    {
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(forwardToSequencer ? new RecordingSequencer() : null);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));
        BlockHeader mined = rpcBlockchain.BlockTree.Head!.Header;
        // Nonce 1 of C is forwarded to the sequencer, or waits in the pool.
        if (forwardToSequencer)
            AssertSucceeded(await SendFromC(rpcBlockchain));
        else
            Assert.That(rpcBlockchain.TxPool.SubmitTx(SignedByC(rpcBlockchain, 1), TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

        string[][] blockParameters = [["pending"], ["latest"], [], [$"0x{mined.Number:x}"], [mined.Hash!.ToString()], [$"0x{mined.Number - 1:x}"]];
        List<string?> counts = [];
        foreach (string[] blockParameter in blockParameters)
            counts.Add((string?)(await TransactionCountOfC(rpcBlockchain, blockParameter))["result"]);

        Assert.That(counts, Is.EqualTo(new[] { "0x2", "0x1", "0x1", "0x1", "0x1", "0x0" }),
            "pending, latest, no block, the mined block by number and by hash, its parent");
    }

    [TestCase(StateFailure.MissingTrieNode, ErrorCodes.ResourceNotFound)]
    [TestCase(StateFailure.StateNotRetained, ErrorCodes.ResourceUnavailable)]
    [TestCase(StateFailure.CorruptTrieNode, ErrorCodes.InternalError)]
    [TestCase(StateFailure.SnapshotNotGathered, ErrorCodes.InternalError)]
    [TestCase(StateFailure.StoreClosed, ErrorCodes.InternalError)]
    public async Task Forwarding_node_reports_no_pending_nonce_without_the_head_state(StateFailure failure, int errorCode)
    {
        HeadState headState = new();
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(new RecordingSequencer(), headState: headState);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));
        headState.Failure = failure;

        JToken pending = await TransactionCountOfC(rpcBlockchain, "pending");
        JToken filled = await FillFromC(rpcBlockchain);
        // With its gas given, the fill estimates nothing, so only the nonce lookup can fail it.
        JToken filledWithoutFrom = await FillWithoutFrom(rpcBlockchain);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((int?)pending["error"]?["code"], Is.EqualTo(errorCode), $"without the head state the pending nonce is unknown, not 0: {pending}");
            Assert.That((int?)filled["error"]?["code"], Is.EqualTo(errorCode), $"without the head state there is no nonce to fill: {filled}");
            Assert.That((int?)filledWithoutFrom["error"]?["code"], Is.EqualTo(errorCode),
                $"a missing from is the zero address, whose nonce is unknown too: {filledWithoutFrom}");
        }
    }

    [Test]
    public async Task Forwarding_node_fills_a_request_without_from_with_the_nonce_of_the_zero_address()
    {
        HeadState headState = new();
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(new RecordingSequencer(), headState: headState);
        // At the head the zero address has nonce 7, and every other account is missing.
        headState.Reader.Configure().TryGetAccount(Arg.Any<BlockHeader?>(), Arg.Any<Address>(), out Arg.Any<AccountStruct>()).Returns(call =>
        {
            bool isZero = call.ArgAt<Address>(1) == Address.Zero;
            call[2] = isZero ? new AccountStruct(7, 0) : default;
            return isZero;
        });

        JToken filled = await FillWithoutFrom(rpcBlockchain);

        Assert.That(FilledNonce(filled), Is.EqualTo("0x7"), filled.ToString());
    }

    [Test]
    public async Task Forwarding_node_read_of_the_pending_nonce_keeps_what_a_send_from_an_older_head_needs()
    {
        RecordingSequencer sequencer = new();
        HeadState headState = new();
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer, headState: headState);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));
        AssertSucceeded(await SendFromC(rpcBlockchain));

        // The second send reads the head nonce 1. Before it looks at the forward of nonce 1, the head includes that
        // forward and a read of the pending nonce, which holds no lock, reads the new head.
        headState.HoldNextRead();
        Task<string> send = Task.Run(() => SendFromC(rpcBlockchain));
        await headState.ReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 1));
        JToken pending = await TransactionCountOfC(rpcBlockchain, "pending");
        headState.ReleaseRead.SetResult();
        AssertSucceeded(await send);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((string?)pending["result"], Is.EqualTo("0x2"), pending.ToString());
            Assert.That(sequencer.ReceivedNonces, Is.EqualTo(new ulong[] { 1, 2 }), "the send must not fill the nonce the head has just included");
        }
    }

    [Test]
    public async Task Forwarding_node_read_of_the_pending_nonce_keeps_a_forward_that_a_later_send_drops([Values] bool fill)
    {
        TaskCompletionSource secondSendArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSecondSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingSequencer sequencer = new(async index =>
        {
            if (index == 1)
            {
                secondSendArrived.SetResult();
                await releaseSecondSend.Task;
            }

            return AcceptedByTheSequencer;
        });
        HeadState headState = new();
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer, headState: headState);
        await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 0));
        AssertSucceeded(await SendFromC(rpcBlockchain));
        ForwardedNonceTracker nonceTracker = ((OptimismEthRpcModule)rpcBlockchain.EthRpcModule).NonceTracker;

        // The read reads the head nonce 1 while the forward of nonce 1 makes the pending nonce 2. Before it looks at that
        // forward, the head includes it and a send, which reads the new head, drops the entry and fills 2.
        headState.HoldNextRead();
        Task<JToken> read = Task.Run(() => fill ? FillFromC(rpcBlockchain, ",\"gas\":\"0x5208\"") : TransactionCountOfC(rpcBlockchain, "pending"));
        Task<string>? send = null;
        string? pendingNonce;
        int entriesWhileTheSendWaits;
        try
        {
            await headState.ReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await rpcBlockchain.AddBlock(SignedByC(rpcBlockchain, 1));
            send = SendFromC(rpcBlockchain);
            await secondSendArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            entriesWhileTheSendWaits = nonceTracker.Count;
            headState.ReleaseRead.SetResult();
            JToken result = await read.WaitAsync(TimeSpan.FromSeconds(30));
            pendingNonce = fill ? FilledNonce(result) : (string?)result["result"];
        }
        finally
        {
            headState.ReleaseRead.TrySetResult();
            releaseSecondSend.TrySetResult();
        }

        AssertSucceeded(await send!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entriesWhileTheSendWaits, Is.Zero, "the send must have dropped the entry the head reached");
            Assert.That(pendingNonce, Is.EqualTo("0x2"), "the pending nonce was 2 for the whole read, first from the forward, then from the head");
            Assert.That(sequencer.ReceivedNonces, Is.EqualTo(new ulong[] { 1, 2 }));
        }
    }

    [Test]
    public async Task Forwarding_node_read_of_the_pending_nonce_is_one_the_account_had_while_a_forward_comes_and_goes_across_a_reorg([Values] bool fill)
    {
        TaskCompletionSource secondSendArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSecondSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingSequencer sequencer = new(async index =>
        {
            if (index == 1)
            {
                secondSendArrived.SetResult();
                await releaseSecondSend.Task;
            }

            return AcceptedByTheSequencer;
        });
        // At the head with number n, C has nonce n; the head starts at 1.
        ReorgingHead head = new();
        TestRpcBlockchain rpcBlockchain = await BuildSendRpc(sequencer, reorgingHead: head);
        ForwardedNonceTracker nonceTracker = ((OptimismEthRpcModule)rpcBlockchain.EthRpcModule).NonceTracker;
        // With gas and its price given, neither the fill nor the sends estimate anything from the moving head.
        const string fields = ",\"gas\":\"0x5208\",\"gasPrice\":\"0x1\"";

        // The read sees no entry, so pending is 1. A send of nonce 1 is accepted: pending 2. A reorg to nonce 0 leaves it
        // 2 through the entry; the head goes to 2 and a second send drops the entry and waits at the sequencer: still 2.
        head.HoldNextHeadLookup();
        Task<JToken> read = Task.Run(() => fill ? FillFromC(rpcBlockchain, fields) : TransactionCountOfC(rpcBlockchain, "pending"));
        Task<string>? secondSend = null;
        string? pendingNonce;
        try
        {
            await head.HeadLookupHeld.Task.WaitAsync(TimeSpan.FromSeconds(30));
            AssertSucceeded(await SendFromC(rpcBlockchain, fields));
            head.MoveTo(0);
            head.HoldNextStateRead();
            head.ReleaseHeadLookup.SetResult();
            await head.StateReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(30));
            head.MoveTo(2);
            secondSend = SendFromC(rpcBlockchain, fields);
            await secondSendArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.That(nonceTracker.Count, Is.Zero, "the second send must have dropped the entry the head reached");
            head.ReleaseStateRead.SetResult();
            JToken result = await read.WaitAsync(TimeSpan.FromSeconds(30));
            pendingNonce = fill ? FilledNonce(result) : (string?)result["result"];
        }
        finally
        {
            head.ReleaseAll();
            releaseSecondSend.TrySetResult();
        }

        AssertSucceeded(await secondSend!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pendingNonce, Is.AnyOf("0x1", "0x2"), "the pending nonce was 1 or 2 for the whole read, never 0");
            Assert.That(sequencer.ReceivedNonces, Is.EqualTo(new ulong[] { 1, 2 }));
        }
    }

    [Test]
    public async Task GetTransactionByHash_ReturnsCorrectTransactionType()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.Legacy)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;
        TxReceipt receipt = new()
        {
            BlockHash = TestItem.KeccakB,
            BlockNumber = 0x10,
            Index = 0x20
        };

        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        TransactionLookupResult lookupResult = new(
            tx,
            new(
                chainId: 0,
                blockHash: receipt.BlockHash!,
                blockNumber: receipt.BlockNumber,
                txIndex: receipt.Index,
                blockTimestamp: 0,
                baseFee: 0,
                receipt: receipt));
        bridge.TryGetTransaction(TestItem.KeccakA, out Arg.Any<TransactionLookupResult?>(), checkTxnPool: true)
            .Returns(callInfo =>
            {
                callInfo[1] = lookupResult;
                return true;
            });

        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithBlockchainBridge(bridge)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: Substitute.For<IJsonRpcClient>(),
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: Substitute.For<IEthereumEcdsa>(),
                sealer: Substitute.For<ITxSealer>(),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();


        string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByHash", TestItem.KeccakA);
        string expected = $$"""
                         {
                            "jsonrpc":"2.0",
                            "result": {
                                 "type": "0x0",
                                 "from": "{{TestItem.AddressA.Bytes.ToHexString(withZeroX: true)}}",
                                 "to": "0x0000000000000000000000000000000000000000",
                                 "value": "0x1",
                                 "gas": "0x5208",
                                 "gasPrice": "0x1",
                                 "input": "0x",
                                 "nonce": "0x0",
                                 "v": "0x0",
                                 "r": "0x0",
                                 "s": "0x0",
                                 "hash": "{{TestItem.KeccakA.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockHash": "{{TestItem.KeccakB.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockNumber": "0x10",
                                 "blockTimestamp": "0x0",
                                 "transactionIndex": "0x20"
                             },
                            "id":67
                         }
                         """;
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task GetTransactionByHash_IncludesDepositReceiptVersion()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;
        OptimismTxReceipt receipt = new()
        {
            BlockHash = TestItem.KeccakB,
            BlockNumber = 0x10,
            Index = 0x20,
            DepositReceiptVersion = 0x30,
        };

        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        TransactionLookupResult lookupResult = new(
            tx,
            new(
                chainId: 0,
                blockHash: receipt.BlockHash!,
                blockNumber: receipt.BlockNumber,
                txIndex: receipt.Index,
                blockTimestamp: 0,
                baseFee: 0,
                receipt: receipt));
        bridge.TryGetTransaction(TestItem.KeccakA, out Arg.Any<TransactionLookupResult?>(), checkTxnPool: true)
            .Returns(callInfo =>
            {
                callInfo[1] = lookupResult;
                return true;
            });

        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithBlockchainBridge(bridge)
            .WithOptimismEthRpcModule(
                sequencerRpcClient: Substitute.For<IJsonRpcClient>(),
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: Substitute.For<IEthereumEcdsa>(),
                sealer: Substitute.For<ITxSealer>(),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();


        string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByHash", TestItem.KeccakA);
        string expected = $$"""
                         {
                            "jsonrpc":"2.0",
                            "result": {
                                 "type": "0x7e",
                                 "sourceHash":"0x0000000000000000000000000000000000000000000000000000000000000000",
                                 "from": "{{TestItem.AddressA.Bytes.ToHexString(withZeroX: true)}}",
                                 "to": "0x0000000000000000000000000000000000000000",
                                 "mint": "0x0",
                                 "value": "0x1",
                                 "gas": "0x5208",
                                 "isSystemTx": false,
                                 "input": "0x",
                                 "nonce": "0x0",
                                 "depositReceiptVersion": "0x30",
                                 "hash": "{{TestItem.KeccakA.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockHash": "{{TestItem.KeccakB.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockNumber": "0x10",
                                 "blockTimestamp": "0x0",
                                 "transactionIndex": "0x20"
                             },
                            "id":67
                         }
                         """;
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task GetTransactionByBlockAndIndex_ReturnsCorrectTransactionType()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.Legacy)
            .WithHash(TestItem.KeccakA)
            .TestObject;
        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader
                .WithNumber(0x10)
                .TestObject)
            .WithTransactions(tx)
            .TestObject;
        OptimismTxReceipt receipt = new()
        {
            TxHash = tx.Hash!,
            BlockHash = block.Hash!,
            BlockNumber = block.Number,
            Index = 0x20,
            DepositReceiptVersion = 0x30,
            Sender = TestItem.AddressA,
        };

        TestRpcBlockchain rpcBlockchain = await BuildOptimismRpc(MockBlockFinder(block), MockReceiptFinder(block, receipt));

        string expected = $$"""
                         {
                            "jsonrpc":"2.0",
                            "result": {
                                "type": "0x0",
                                "from": "{{TestItem.AddressA.Bytes.ToHexString(withZeroX: true)}}",
                                "to": "0x0000000000000000000000000000000000000000",
                                "value": "0x1",
                                "gas": "0x5208",
                                "gasPrice": "0x1",
                                "input": "0x",
                                "nonce": "0x0",
                                "v": "0x0",
                                "r": "0x0",
                                "s": "0x0",
                                "hash": "{{tx.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                "blockHash": "{{block.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                "blockNumber": "0x10",
                                "blockTimestamp": "0xf4240",
                                "transactionIndex": "0x0"
                            },
                            "id":67
                         }
                         """;
        {
            // By block hash
            string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByBlockHashAndIndex", block.Hash, "0x0");
            Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
        }
        {
            // By block number
            string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByBlockNumberAndIndex", block.Number, "0x0");
            Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
        }
    }

    [Test]
    public async Task GetTransactionByBlockAndIndex_IncludesDepositReceiptVersion()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;
        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader
                .WithNumber(0x10)
                .TestObject)
            .WithTransactions(tx)
            .TestObject;
        OptimismTxReceipt receipt = new()
        {
            TxHash = tx.Hash!,
            BlockHash = block.Hash!,
            BlockNumber = block.Number,
            Index = 0x20,
            DepositReceiptVersion = 0x30,
        };

        TestRpcBlockchain rpcBlockchain = await BuildOptimismRpc(MockBlockFinder(block), MockReceiptFinder(block, receipt));

        string expected = $$"""
                         {
                            "jsonrpc":"2.0",
                            "result": {
                                 "type": "0x7e",
                                 "sourceHash":"0x0000000000000000000000000000000000000000000000000000000000000000",
                                 "from": "{{tx.SenderAddress!.Bytes.ToHexString(withZeroX: true)}}",
                                 "to": "0x0000000000000000000000000000000000000000",
                                 "mint": "0x0",
                                 "value": "0x1",
                                 "gas": "0x5208",
                                 "isSystemTx": false,
                                 "input": "0x",
                                 "nonce": "0x0",
                                 "depositReceiptVersion": "0x30",
                                 "hash": "{{tx.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockHash": "{{block.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                 "blockNumber": "0x10",
                                 "blockTimestamp": "0xf4240",
                                 "transactionIndex": "0x0"
                             },
                            "id":67
                         }
                         """;
        {
            // By block hash
            string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByBlockHashAndIndex", block.Hash, "0x0");
            Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
        }
        {
            // By block number
            string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionByBlockNumberAndIndex", block.Number, "0x0");
            Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
        }
    }

    public enum ReceiptOrder { Aligned, Reversed, Empty }

    [TestCase(ReceiptOrder.Aligned, "0x1", TestName = "Receipts aligned with transactions (fast path)")]
    [TestCase(ReceiptOrder.Reversed, "0x1", TestName = "Receipts in reverse order (linear fallback)")]
    [TestCase(ReceiptOrder.Empty, null, TestName = "Empty receipts (no match)")]
    public async Task GetBlockByHash_WithFullTransactions_SetsDepositReceiptVersionFromMatchingReceipt(
        ReceiptOrder order, string? expectedDepositVersion)
    {
        Transaction depositTx = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;
        Transaction regularTx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithHash(TestItem.KeccakB)
            .WithSenderAddress(TestItem.AddressB)
            .TestObject;

        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader
                .WithNumber(1)
                .WithHash(TestItem.KeccakC)
                .TestObject)
            .WithTransactions(depositTx, regularTx)
            .TestObject;

        OptimismTxReceipt depositReceipt = new()
        {
            Sender = depositTx.SenderAddress!,
            TxType = depositTx.Type,
            TxHash = depositTx.Hash!,
            BlockHash = block.Hash,
            BlockNumber = block.Number,
            Index = 0,
            DepositReceiptVersion = 1,
        };
        TxReceipt regularReceipt = new()
        {
            Sender = regularTx.SenderAddress!,
            TxType = regularTx.Type,
            TxHash = regularTx.Hash!,
            BlockHash = block.Hash,
            BlockNumber = block.Number,
            Index = 1,
        };

        TxReceipt[] receipts = order switch
        {
            ReceiptOrder.Aligned => [depositReceipt, regularReceipt],
            ReceiptOrder.Reversed => [regularReceipt, depositReceipt],
            ReceiptOrder.Empty => [],
            _ => throw new System.ArgumentOutOfRangeException(nameof(order)),
        };

        TestRpcBlockchain rpcBlockchain = await BuildOptimismRpc(MockBlockFinder(block), MockReceiptFinder(block, receipts));

        string serialized = await rpcBlockchain.TestEthRpc("eth_getBlockByHash", block.Hash, true);
        JToken result = JToken.Parse(serialized)["result"]!;
        JToken firstTx = result["transactions"]![0]!;
        JToken secondTx = result["transactions"]![1]!;

        Assert.That(firstTx["hash"]!.Value<string>(), Is.EqualTo(depositTx.Hash!.Bytes.ToHexString(withZeroX: true)));
        if (expectedDepositVersion is null)
            Assert.That(firstTx["depositReceiptVersion"], Is.Null);
        else
            Assert.That(firstTx["depositReceiptVersion"]!.Value<string>(), Is.EqualTo(expectedDepositVersion));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondTx["hash"]!.Value<string>(), Is.EqualTo(regularTx.Hash!.Bytes.ToHexString(withZeroX: true)));
            Assert.That(secondTx["depositReceiptVersion"], Is.Null);
        }
    }

    [Test]
    public async Task GetBlockReceipts_logIndex_follows_eip8116([Values] bool eip8116Enabled)
    {
        Transaction depositTx = Build.A.Transaction.WithType(TxType.DepositTx).WithHash(TestItem.KeccakA).WithSenderAddress(TestItem.AddressA).TestObject;
        Transaction regularTx = Build.A.Transaction.WithType(TxType.EIP1559).WithHash(TestItem.KeccakB).WithSenderAddress(TestItem.AddressA).TestObject;
        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakC).TestObject)
            .WithTransactions(depositTx, regularTx)
            .TestObject;
        LogEntry[] logs = [Build.A.LogEntry.TestObject, Build.A.LogEntry.TestObject];
        OptimismTxReceipt depositReceipt = new() { Sender = TestItem.AddressA, TxType = depositTx.Type, TxHash = depositTx.Hash!, BlockHash = block.Hash, BlockNumber = 1, Index = 0, Logs = logs };
        TxReceipt regularReceipt = new() { Sender = TestItem.AddressA, TxType = regularTx.Type, TxHash = regularTx.Hash!, BlockHash = block.Hash, BlockNumber = 1, Index = 1, Logs = logs };
        IReleaseSpec spec = eip8116Enabled ? new OverridableReleaseSpec(Bogota.Instance) { IsEip8116Enabled = true } : Bogota.Instance;

        using TestRpcBlockchain rpcBlockchain = await BuildOptimismRpc(MockBlockFinder(block), MockReceiptFinder(block, depositReceipt, regularReceipt), new TestSpecProvider(spec));

        JToken result = JToken.Parse(await rpcBlockchain.TestEthRpc("eth_getBlockReceipts", new BlockParameter(block.Number)))["result"]!;
        string[] secondReceiptLogIndexes = eip8116Enabled ? ["0x0", "0x1"] : ["0x2", "0x3"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result[0]!["logs"]!.Select(static l => (string)l["logIndex"]!), Is.EqualTo(new[] { "0x0", "0x1" }));
            Assert.That(result[1]!["logs"]!.Select(static l => (string)l["logIndex"]!), Is.EqualTo(secondReceiptLogIndexes));
        }
    }

    [Test]
    public async Task GetBlockReceipts_ReturnsDefaultAndOptimismReceipts()
    {
        Transaction txA = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;

        Transaction txB = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithHash(TestItem.KeccakB)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;

        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader
                .WithNumber(1)
                .WithHash(TestItem.KeccakC)
                .TestObject)
            .WithTransactions(txA, txB)
            .TestObject;

        OptimismTxReceipt receiptA = new()
        {
            Sender = txA.SenderAddress!,
            TxType = txA.Type,
            TxHash = txA.Hash!,
            BlockHash = block.Hash,
            BlockNumber = 1,
            Index = 0,
            DepositReceiptVersion = 1,
            DepositNonce = 2,
        };

        TxReceipt receiptB = new()
        {
            Sender = txB.SenderAddress!,
            TxType = txB.Type,
            TxHash = txB.Hash!,
            BlockHash = block.Hash,
            BlockNumber = 1,
            Index = 1,
        };

        TestRpcBlockchain rpcBlockchain = await BuildOptimismRpc(MockBlockFinder(block), MockReceiptFinder(block, receiptA, receiptB));

        string serialized = await rpcBlockchain.TestEthRpc("eth_getBlockReceipts", new BlockParameter(block.Number));
        string expected = $$"""
                         {
                            "jsonrpc":"2.0",
                            "result": [
                                {
                                    "transactionHash": "{{txA.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                    "transactionIndex": "0x0",
                                    "blockHash": "{{block.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                    "blockNumber": "0x1",
                                    "cumulativeGasUsed": "0x0",
                                    "gasUsed": "0x0",
                                    "effectiveGasPrice": "0x1",
                                    "from": "{{txA.SenderAddress!.Bytes.ToHexString(withZeroX: true)}}",
                                    "to": null,
                                    "contractAddress": null,
                                    "logs": [],
                                    "logsBloom": "0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                                    "status": "0x0",
                                    "type": "0x7e",
                                    "depositReceiptVersion": "0x1",
                                    "depositNonce": "0x2",
                                },
                                {
                                    "transactionHash": "{{txB.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                    "transactionIndex": "0x1",
                                    "blockHash": "{{block.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                    "blockNumber": "0x1",
                                    "cumulativeGasUsed": "0x0",
                                    "gasUsed": "0x0",
                                    "effectiveGasPrice": "0x1",
                                    "from": "{{txA.SenderAddress!.Bytes.ToHexString(withZeroX: true)}}",
                                    "to": null,
                                    "contractAddress": null,
                                    "logs": [],
                                    "logsBloom": "0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                                    "status": "0x0",
                                    "type": "0x2"
                                }
                            ],
                            "id":67
                         }
                         """;
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    [Test]
    public async Task GetBlockReceipts_fails_when_the_receipts_of_a_block_with_transactions_are_missing([Values] bool canonical, [Values] bool byHash)
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).WithHash(TestItem.KeccakA).WithSenderAddress(TestItem.AddressA).TestObject;
        Block block = Build.A.Block.WithHeader(Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakC).TestObject).WithTransactions(tx).TestObject;
        IBlockFinder blockFinder = MockBlockFinder(block);
        if (canonical)
        {
            blockFinder.Head.Returns(block);
            blockFinder.IsMainChain(block.Header).Returns(true);
        }

        using TestRpcBlockchain rpcBlockchain = await BuildOptimismRpc(blockFinder, MockReceiptFinder(block));

        string serialized = await rpcBlockchain.TestEthRpc("eth_getBlockReceipts", byHash ? new BlockParameter(block.Hash!) : new BlockParameter(block.Number));

        Assert.That(JToken.Parse(serialized)["error"]?["code"]?.Value<int>(),
            Is.EqualTo(canonical ? ErrorCodes.PrunedHistoryUnavailable : ErrorCodes.ResourceNotFound), serialized);
    }

    [Test]
    public async Task GetTransactionReceipt_ReturnsDepositReceipt()
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .TestObject;

        ulong timestamp = 10;
        Block block = Build.A.Block
            .WithHeader(Build.A.BlockHeader
                .WithNumber(1)
                .WithHash(TestItem.KeccakC)
                .TestObject)
            .WithTimestamp(timestamp)
            .WithTransactions(tx)
            .TestObject;

        OptimismTxReceipt receipt = new()
        {
            Sender = tx.SenderAddress!,
            TxType = tx.Type,
            TxHash = tx.Hash!,
            BlockHash = block.Hash,
            BlockNumber = 1,
            Index = 0,
            DepositReceiptVersion = 1,
        };

        IBlockchainBridge bridge = Substitute.For<IBlockchainBridge>();
        bridge.GetTxReceiptInfo(tx.Hash!).Returns((receipt, timestamp, new TxGasInfo(1), 0));

        TestRpcBlockchain rpcBlockchain = await TestRpcBlockchain
            .ForTest(sealEngineType: SealEngineType.Optimism)
            .WithBlockchainBridge(bridge)
            .WithBlockFinder(MockBlockFinder(block))
            .WithOptimismEthRpcModule(
                sequencerRpcClient: Substitute.For<IJsonRpcClient>(),
                accountStateProvider: Substitute.For<IAccountStateProvider>(),
                ecdsa: Substitute.For<IEthereumEcdsa>(),
                sealer: Substitute.For<ITxSealer>(),
                opSpecHelper: Substitute.For<IOptimismSpecHelper>())
            .Build();


        string serialized = await rpcBlockchain.TestEthRpc("eth_getTransactionReceipt", tx.Hash);
        string expected = $$"""
                         {
                            "jsonrpc":"2.0",
                            "result": {
                                "transactionHash": "{{tx.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                "transactionIndex": "0x0",
                                "blockHash": "{{block.Hash!.Bytes.ToHexString(withZeroX: true)}}",
                                "blockNumber": "0x1",
                                "cumulativeGasUsed": "0x0",
                                "gasUsed": "0x0",
                                "effectiveGasPrice": "0x1",
                                "from": "{{tx.SenderAddress!.Bytes.ToHexString(withZeroX: true)}}",
                                "to": null,
                                "contractAddress": null,
                                "logs": [],
                                "logsBloom": "0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                                "status": "0x0",
                                "type": "0x7e",
                                "depositReceiptVersion": "0x1",
                            },
                            "id":67
                         }
                         """;
        Assert.That(JToken.Parse(serialized), Is.EqualTo(JToken.Parse(expected)).Using(JToken.EqualityComparer));
    }

    private const string NullResult = """{"jsonrpc":"2.0","result":null,"id":67}""";
    private const ulong LowestServedBlock = 5;

    /// <summary>A finder with a head and a served floor above a known header whose body it does not have.</summary>
    private static IBlockFinder FinderWithoutTheBodyOf(Block pruned)
    {
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.WithNumber(LowestServedBlock + 10).TestObject);
        finder.LowestServedBlock.Returns(LowestServedBlock);
        finder.FindHeader(new BlockParameter(pruned.Hash!)).Returns(pruned.Header);
        return finder;
    }

    private static Task<string> CallWithBlock(TestRpcBlockchain rpc, string method, object selector) => method switch
    {
        "eth_getBlockByNumber" => rpc.TestEthRpc(method, selector, false),
        "eth_getTransactionByBlockNumberAndIndex" => rpc.TestEthRpc(method, selector, "0x0"),
        _ => rpc.TestEthRpc(method, selector),
    };

    [TestCase("eth_getBlockReceipts", false)]
    [TestCase("eth_getBlockReceipts", true)]
    [TestCase("eth_getBlockByNumber", false)]
    [TestCase("eth_getBlockByNumber", true)]
    [TestCase("eth_getTransactionByBlockNumberAndIndex", false)]
    [TestCase("eth_getTransactionByBlockNumberAndIndex", true)]
    public async Task Block_read_of_a_body_pruned_below_the_lowest_served_block_returns_pruned_history(string method, bool byHash)
    {
        Block block = Build.A.Block.WithNumber(LowestServedBlock - 2).TestObject;
        using TestRpcBlockchain rpc = await BuildOptimismRpc(FinderWithoutTheBodyOf(block), Substitute.For<IReceiptFinder>());

        string serialized = await CallWithBlock(rpc, method, byHash ? block.Hash! : new BlockParameter(block.Number));

        Assert.That(serialized, Is.EqualTo($$"""{"jsonrpc":"2.0","error":{"code":4444,"message":"Pruned history unavailable for block {{(byHash ? block.Hash : block.Number)}}"},"id":67}"""));
    }

    private const string UnknownHash = "0x0000000000000000000000000000000000000000000000000000000000000001";
    private const string HeaderNotFound = """{"jsonrpc":"2.0","error":{"code":-32000,"message":"header not found"},"id":67}""";
    private const string UnknownBlock = """{"jsonrpc":"2.0","error":{"code":-39001,"message":"Unknown block error"},"id":67}""";

    [TestCase("eth_getBlockReceipts", "0x7", NullResult)]
    [TestCase("eth_getBlockReceipts", UnknownHash, NullResult)]
    [TestCase("eth_getBlockReceipts", "safe", NullResult)]
    [TestCase("eth_getBlockReceipts", "finalized", NullResult)]
    [TestCase("eth_getBlockByNumber", "0x7", NullResult)]
    [TestCase("eth_getBlockByNumber", UnknownHash, NullResult)]
    [TestCase("eth_getBlockByNumber", "safe", UnknownBlock)]
    [TestCase("eth_getBlockByNumber", "finalized", UnknownBlock)]
    [TestCase("eth_getTransactionByBlockNumberAndIndex", "0x7", HeaderNotFound)]
    [TestCase("eth_getTransactionByBlockNumberAndIndex", UnknownHash, HeaderNotFound)]
    [TestCase("eth_getTransactionByBlockNumberAndIndex", "safe", UnknownBlock)]
    [TestCase("eth_getTransactionByBlockNumberAndIndex", "finalized", UnknownBlock)]
    public async Task Block_read_of_a_block_the_node_does_not_know_keeps_its_answer(string method, string selector, string expected)
    {
        Block block = Build.A.Block.WithNumber(LowestServedBlock - 2).TestObject;
        using TestRpcBlockchain rpc = await BuildOptimismRpc(FinderWithoutTheBodyOf(block), Substitute.For<IReceiptFinder>());

        string serialized = await CallWithBlock(rpc, method, selector);

        Assert.That(serialized, Is.EqualTo(expected));
    }
}

internal static class TestRpcBlockchainExt
{
    public static TestRpcBlockchain.Builder<TestRpcBlockchain> WithOptimismEthRpcModule(
        this TestRpcBlockchain.Builder<TestRpcBlockchain> @this,
        IJsonRpcClient? sequencerRpcClient,
        IAccountStateProvider accountStateProvider,
        IEthereumEcdsa ecdsa,
        ITxSealer sealer,
        IOptimismSpecHelper opSpecHelper,
        IStateReader? nonceStateReader = null) =>
        @this.WithEthRpcModule(blockchain => new OptimismEthRpcModule(
            blockchain.RpcConfig,
            blockchain.Bridge,
            blockchain.BlockFinder,
            blockchain.BlockTree,
            blockchain.ReceiptFinder,
            blockchain.StateReader,
            // As OptimismPlugin wires it: a node that forwards to the sequencer has no pool.
            sequencerRpcClient is null ? blockchain.TxPool : NullTxPool.Instance,
            blockchain.TxSender,
            blockchain.TestWallet,
            LimboLogs.Instance,
            blockchain.SpecProvider,
            blockchain.GasPriceOracle,
            new EthSyncingInfo(blockchain.BlockTree, Substitute.For<ISyncPointers>(), new SyncConfig(),
                new StaticSelector(SyncMode.All), Substitute.For<ISyncProgressResolver>(), Synchronization.No.BeaconSync, blockchain.LogManager),
            blockchain.FeeHistoryOracle ??
            new FeeHistoryOracle(blockchain.BlockTree, blockchain.ReceiptStorage, blockchain.SpecProvider, blockchain.RpcConfig),
            blockchain.ProtocolsManager,
            blockchain.ForkInfo,
            new BlocksConfig().SecondsPerSlot,
            sequencerRpcClient, ecdsa, sealer, opSpecHelper,
            new HeadBlockSignal(blockchain.BlockTree),
            new EthCapabilitiesProvider(
                blockchain.BlockTree.AsReadOnly(),
                blockchain.Container.Resolve<IStateBoundary>(),
                blockchain.Container.Resolve<ISyncConfig>(),
                Substitute.For<ISyncPointers>(),
                Substitute.For<IHistoryConfig>(),
                Substitute.For<IHistoryPruner>()),
            new BlockForRpcFactory(),
            new ForwardedNonceTracker(blockchain.BlockFinder, nonceStateReader ?? blockchain.StateReader)
        ));
}
