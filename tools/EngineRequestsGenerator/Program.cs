using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ethereum.Test.Base;
using Evm.JsonTypes;
using Evm.T8NTool;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualBasic;
using Nethermind.Blockchain;
using Nethermind.Consensus.BeaconBlockRoot;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Test;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Network.P2P.Subprotocols.Les.Messages;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Trie.Pruning;

namespace EngineRequestsGenerator;

public static class Program
{
    static async Task Main(string[] args)
    {
        StringBuilder stringBuilder = new();
        EthereumJsonSerializer serializer = new();
        ILogManager logManager = LimboLogs.Instance;
        ITimestamper timestamper = new IncrementalTimestamper();



        string inputAlloc = File.ReadAllText("../../../data/holeskyGenesisAlloc.json");
        Dictionary<Address, AccountState> allocJson = serializer.Deserialize<Dictionary<Address, AccountState>>(inputAlloc);

        // string inputEnv = File.ReadAllText("../../../data/holeskyGenesisEnv.json");
        //
        // EnvInfo envInfo = serializer.Deserialize<EnvInfo>(inputEnv);


        // EngineModuleTests.MergeTestBlockchain chain = await CreateBlockchain(releaseSpec: Cancun.Instance);

        // IReleaseSpec spec = Cancun.Instance;
        // ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, spec), ((ForkActivation)envInfo.CurrentNumber, spec));


        EngineModuleTests.MergeTestBlockchain chain = await new EngineModuleTests.MergeTestBlockchain().Build(true, HoleskySpecProvider.Instance);
        // chain.Timestamper = timestamper;

        ChainSpecLoader chainSpecLoader = new ChainSpecLoader(serializer);
        ChainSpec chainSpec = chainSpecLoader.LoadEmbeddedOrFromFile("../../../../../src/Nethermind/Chains/holesky.json", LimboLogs.Instance.GetClassLogger());

        GenesisLoader genesisLoader = new GenesisLoader(chainSpec, HoleskySpecProvider.Instance, chain.State, chain.TxProcessor);
        Block genesisBlock = genesisLoader.Load();


        Withdrawal withdrawal = new()
        {
            Address = TestItem.AddressA,
            AmountInGwei = 1000,
            ValidatorIndex = 1,
            Index = 1
        };

        PayloadAttributes payloadAttributes = new PayloadAttributes()
        {
            Timestamp = genesisBlock.Timestamp + 7,
            ParentBeaconBlockRoot = genesisBlock.Hash,
            PrevRandao = genesisBlock.Hash ?? Keccak.Zero,
            SuggestedFeeRecipient = Address.Zero,
            Withdrawals = new []{withdrawal}
        };

        // Hash256 parentOfGenesisHash = new("0x0000000000000000000000000000000000000000000000000000000000000000");
        // Hash256 genesisHash = new("0x9cbea0de83b440f4462c8280a4b0b4590cdb452069757e2c510cb3456b6c98cc");


        // BlockHeader genesisHeader = new BlockHeader(parentOfGenesisHash, Keccak.OfAnEmptySequenceRlp, Address.Zero, 1, 0,
        //     25000000000, 1695902100, []);


        // if (specProvider?.GenesisSpec?.IsBeaconBlockRootAvailable ?? false)
        // {
        //     chain.State.CreateAccount(specProvider.GenesisSpec.Eip4788ContractAddress, 1);
        // }

        // GeneralStateTestBase.InitializeTestPreState(allocJson, (WorldState)chain.State, HoleskySpecProvider.Instance);

        // genesisHeader.StateRoot = chain.State.StateRoot;
        // genesisHeader.Hash = genesisHeader.CalculateHash();


        stringBuilder.Append("gen hash: ");
        stringBuilder.Append(genesisBlock.Hash);
        stringBuilder.AppendLine();
        stringBuilder.Append("genesis stateRoot: ");
        stringBuilder.Append(genesisBlock.StateRoot);
        stringBuilder.AppendLine();
        stringBuilder.Append("stater root: ");
        stringBuilder.Append(chain.State.StateRoot);
        File.WriteAllText("requests.txt", stringBuilder.ToString());

        Block block = chain.PostMergeBlockProducer.PrepareEmptyBlock(genesisBlock.Header, payloadAttributes);

        // chain.PayloadPreparationService.StartPreparingPayload(genesisBlock.Header, payloadAttributes);
        // Block block = chain.PayloadPreparationService.GetPayload(payloadAttributes.GetPayloadId(genesisBlock.Header)).Result.CurrentBestBlock;

        ExecutionPayload executionPayload = new ExecutionPayloadV3(block);

        // IEngineRpcModule rpcModule = CreateEngineModule(chain);
        // JsonRpcConfig jsonRpcConfig = new() { EnabledModules = new[] { ModuleType.Engine } };
        // RpcModuleProvider moduleProvider = new(new FileSystem(), jsonRpcConfig, LimboLogs.Instance);
        // moduleProvider.Register(new SingletonModulePool<IEngineRpcModule>(new SingletonFactory<IEngineRpcModule>(rpcModule), true));

        string executionPayloadString = serializer.Serialize(executionPayload);
        string blobsString = serializer.Serialize(Array.Empty<byte[]>());
        string parentBeaconBlockRootString = TestItem.KeccakA.ToString();

        JsonObject executionPayloadAsJObject = serializer.Deserialize<JsonObject>(executionPayloadString);
        JsonRpcRequest request = RpcTest.GetJsonRequest(nameof(IEngineRpcModule.engine_newPayloadV3),
                serializer.Serialize(executionPayloadAsJObject), blobsString, parentBeaconBlockRootString);

        string requestString = serializer.Serialize(request);

        File.WriteAllText("requests.txt", requestString);


        // ExecutionPayloadV3 executionPayload = CreateBlockRequestV3(
        //     chain, CreateParentBlockRequestOnHead(chain.BlockTree), TestItem.AddressD, withdrawals: Array.Empty<Withdrawal>(), blobGasUsed: 0, excessBlobGas: 0, parentBeaconBlockRoot: TestItem.KeccakA);











        // Dictionary<Address, AccountState> allocJson = serializer.Deserialize<Dictionary<Address, AccountState>>(inputAlloc);
        // EnvInfo envInfo = serializer.Deserialize<EnvInfo>(inputEnv);
        //
        // Transaction[] transactions = [];
        // // if (inputTxs.EndsWith(".json")) {
        // //     var txInfoList = _ethereumJsonSerializer.Deserialize<TransactionForRpc[]>(File.ReadAllText(inputTxs));
        // //     transactions = txInfoList.Select(txInfo => txInfo.ToTransaction()).ToArray();
        // // } else {
        // //     string rlpRaw = File.ReadAllText(inputTxs).Replace("\"", "").Replace("\n", "");
        // //     RlpStream rlp = new(Bytes.FromHexString(rlpRaw));
        // //     transactions = _txDecoder.DecodeArray(rlp);
        // // }
        //
        // IDb stateDb = new MemDb();
        // IDb codeDb = new MemDb();
        //
        // IReleaseSpec spec = Cancun.Instance;
        // ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, spec), ((ForkActivation)envInfo.CurrentNumber, spec));
        // specProvider.UpdateMergeTransitionInfo(envInfo.CurrentNumber, 0);
        //
        // TrieStore trieStore = new(stateDb, logManager);
        // WorldState stateProvider = new(trieStore, codeDb, logManager);
        //
        // var blockhashProvider = new T8NBlockHashProvider();
        // IVirtualMachine virtualMachine = new VirtualMachine(
        //     blockhashProvider,
        //     specProvider,
        //     logManager);
        //
        // TransactionProcessor transactionProcessor = new(
        //     specProvider,
        //     stateProvider,
        //     virtualMachine,
        //     logManager);
        //
        // GeneralStateTestBase.InitializeTestPreState(allocJson, stateProvider, specProvider);
        //
        // var ecdsa = new EthereumEcdsa(specProvider.ChainId, logManager);
        // // foreach (var transaction in transactions)
        // // {
        // //     transaction.SenderAddress = ecdsa.RecoverAddress(transaction);
        // // }
        //
        // envInfo.ApplyChecks(specProvider, spec);
        //
        // var header = envInfo.GetBlockHeader();
        // var parent = envInfo.GetParentBlockHeader();
        //
        // header.IsPostMerge = true;
        //
        // if (header.Hash != null) blockhashProvider.Insert(header.Hash, header.Number);
        // if (parent.Hash != null) blockhashProvider.Insert(parent.Hash, parent.Number);
        // foreach (var envJsonBlockHash in envInfo.BlockHashes)
        // {
        //     blockhashProvider.Insert(envJsonBlockHash.Value, long.Parse(envJsonBlockHash.Key));
        // }
        //
        // TxValidator txValidator = new(MainnetSpecProvider.Instance.ChainId);
        // IReceiptSpec receiptSpec = specProvider.GetSpec(header);
        // header.ExcessBlobGas ??= BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
        //
        // List<Transaction> successfulTxs = [];
        // List<TxReceipt> includedTxReceipts = [];
        //
        // BlockHeader[] uncles = envInfo.Ommers
        //     .Select(ommer => Build.A.BlockHeader
        //         .WithNumber(envInfo.CurrentNumber - ommer.Delta)
        //         .WithBeneficiary(ommer.Address)
        //         .TestObject)
        //     .ToArray();
        //
        // Block block = Build.A.Block.WithHeader(header).WithTransactions(transactions).WithWithdrawals(envInfo.Withdrawals).WithUncles(uncles).TestObject;
        // new BeaconBlockRootHandler().ApplyContractStateChanges(block, spec, stateProvider);
        //
        //
        // ApplyReward(block, stateProvider, spec, specProvider);
        //
        // T8NToolTracer tracer = new();
        // tracer.StartNewBlockTrace(block);
        // var withdrawalProcessor = new WithdrawalProcessor(stateProvider, logManager);
        // withdrawalProcessor.ProcessWithdrawals(block, spec);
        // stateProvider.Commit(spec);
        // stateProvider.RecalculateStateRoot();
        //
        // List<RejectedTx> rejectedTxReceipts = [];
        // int txIndex = 0;
        // List<Transaction> includedTx = [];
        // foreach (Transaction tx in transactions)
        // {
        //     string? error;
        //     bool isValid = txValidator.IsWellFormed(tx, spec, out error);
        //     if (isValid)
        //     {
        //         tracer.StartNewTxTrace(tx);
        //         TransactionResult transactionResult = transactionProcessor.Execute(tx, new BlockExecutionContext(header), tracer);
        //         tracer.EndTxTrace();
        //         includedTx.Add(tx);
        //
        //         if (transactionResult.Success)
        //         {
        //             successfulTxs.Add(tx);
        //             tracer.LastReceipt.PostTransactionState = null;
        //             tracer.LastReceipt.BlockHash = null;
        //             tracer.LastReceipt.BlockNumber = 0;
        //             includedTxReceipts.Add(tracer.LastReceipt);
        //         } else if (transactionResult.Error != null)
        //         {
        //             rejectedTxReceipts.Add(new RejectedTx(txIndex,  GethErrorMappings.GetErrorMapping(transactionResult.Error, tx.SenderAddress.ToString(true), tx.Nonce, stateProvider.GetNonce(tx.SenderAddress))));
        //             stateProvider.Reset();
        //         }
        //         stateProvider.RecalculateStateRoot();
        //         txIndex++;
        //     }
        //     else if (error != null)
        //     {
        //         rejectedTxReceipts.Add(new RejectedTx(txIndex, GethErrorMappings.GetErrorMapping(error)));
        //     }
        // }
        // if (spec.IsEip4844Enabled)
        // {
        //     block.Header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(includedTx.ToArray());
        // }
        //
        // ulong gasUsed = 0;
        // if (!tracer.TxReceipts.IsNullOrEmpty())
        // {
        //      gasUsed = (ulong) tracer.LastReceipt.GasUsedTotal;
        // }
        //
        // Hash256 stateRoot = stateProvider.StateRoot;
        // Hash256 txRoot = TxTrie.CalculateRoot(successfulTxs.ToArray());
        // Hash256 receiptsRoot = ReceiptTrie<TxReceipt>.CalculateRoot(receiptSpec, includedTxReceipts.ToArray(), ReceiptMessageDecoder.Instance);
        //
        // var logEntries = includedTxReceipts.SelectMany(receipt => receipt.Logs ?? Enumerable.Empty<LogEntry>()).ToArray();
        // var bloom = new Bloom(logEntries);
        //
        // BlockHeader blockHeader = new BlockHeader()
        // var postState = new PostState
        // {
        //     StateRoot = stateRoot,
        //     TxRoot = txRoot,
        //     ReceiptsRoot = receiptsRoot,
        //     LogsBloom = bloom,
        //     LogsHash = Keccak.Compute(Rlp.OfEmptySequence.Bytes),
        //     Receipts = includedTxReceipts.ToArray(),
        //     Rejected = rejectedTxReceipts.IsNullOrEmpty() ? null : rejectedTxReceipts.ToArray(),
        //     CurrentDifficulty = envInfo.CurrentDifficulty,
        //     GasUsed = new UInt256(gasUsed),
        //     CurrentBaseFee = envInfo.CurrentBaseFee,
        //     WithdrawalsRoot = block.WithdrawalsRoot,
        //     CurrentExcessBlobGas = header.ExcessBlobGas,
        //     BlobGasUsed = header.BlobGasUsed
        // };
        //
        // var accounts = allocJson.Keys.ToDictionary(address => address, address => AccountState.GetFromAccount(address, stateProvider, tracer.storages));
        // foreach (Ommer ommer in envInfo.Ommers)
        // {
        //     accounts.Add(ommer.Address, AccountState.GetFromAccount(ommer.Address, stateProvider, tracer.storages));
        // }
        // if (header.Beneficiary != null)
        // {
        //     accounts.Add(header.Beneficiary, AccountState.GetFromAccount(header.Beneficiary, stateProvider, tracer.storages));
        // }
        //
        // accounts = accounts.Where(account => !account.Value.IsEmptyAccount()).ToDictionary();
        // var body = Rlp.Encode(successfulTxs.ToArray()).Bytes;







        // T8NTool t8NTool = new();
        //
        // string inputAlloc = File.ReadAllText("../../../../data/holeskyGenesisAlloc.json");
        // string inputEnv = File.ReadAllText("../../../../data/holeskyGenesisEnv.json");
        //
        // T8NOutput t8NOutput = t8NTool.Execute(inputAlloc, inputEnv, "", null, null, null, null, 17000, "cancun", null, false, false, false,
        //     false, false);
        //
        // Hash256 parentHash = new("0x9cbea0de83b440f4462c8280a4b0b4590cdb452069757e2c510cb3456b6c98cc");
        //
        //
        // BlockHeader blockHeader = new BlockHeader(parentHash, Keccak.EmptyTreeHash,);
        // Block block = new Block(blockHeader);
        // ExecutionPayload payload = new ExecutionPayload(block);
        // JsonRpcRequest request1 = GetJsonRpcRequest("method");

        // string executionPayloadString = serializer.Serialize(payload);
        // string blobsString = serializer.Serialize(Array.Empty<byte[]>());
        // string parentBeaconBlockRootString = TestItem.KeccakA.ToString();

        // WriteJsonRpcRequest(stringBuilder, nameof(IEngineRpcModule.engine_newPayloadV3), new []{executionPayloadString, blobsString, parentBeaconBlockRootString});
        // JsonObject executionPayloadAsJObject = serializer.Deserialize<JsonObject>(executionPayloadString);
        // JsonRpcRequest request = RpcTest.GetJsonRequest(nameof(IEngineRpcModule.engine_newPayloadV3),
        //         serializer.Serialize(executionPayloadAsJObject), blobsString, parentBeaconBlockRootString);

        // string jsonString = serializer.Serialize(request1);
        // File.WriteAllText("requests.txt", stringBuilder.ToString());
    }


    // private static ExecutionPayloadV3 CreateBlockRequestV3(EngineModuleTests.MergeTestBlockchain chain, ExecutionPayload parent, Address miner, Withdrawal[]? withdrawals = null,
    //             ulong? blobGasUsed = null, ulong? excessBlobGas = null, Transaction[]? transactions = null, Hash256? parentBeaconBlockRoot = null)
    // {
    //     IBeaconBlockRootHandler beaconBlockRootHandler = new BeaconBlockRootHandler();
    //
    //     ExecutionPayloadV3 blockRequestV3 = CreateBlockRequestInternal<ExecutionPayloadV3>(parent, miner, withdrawals, blobGasUsed, excessBlobGas, transactions: transactions, parentBeaconBlockRoot: parentBeaconBlockRoot);
    //     blockRequestV3.TryGetBlock(out Block? block);
    //
    //     Snapshot before = chain.State.TakeSnapshot();
    //     beaconBlockRootHandler.ApplyContractStateChanges(block!, chain.SpecProvider.GenesisSpec, chain.State);
    //     chain.WithdrawalProcessor?.ProcessWithdrawals(block!, chain.SpecProvider.GenesisSpec);
    //
    //     chain.State.Commit(chain.SpecProvider.GenesisSpec);
    //     chain.State.RecalculateStateRoot();
    //     blockRequestV3.StateRoot = chain.State.StateRoot;
    //     chain.State.Restore(before);
    //
    //     TryCalculateHash(blockRequestV3, out Hash256? hash);
    //     blockRequestV3.BlockHash = hash;
    //     return blockRequestV3;
    // }
    //
    // private static T CreateBlockRequestInternal<T>(ExecutionPayload parent, Address miner, Withdrawal[]? withdrawals = null,
    //             ulong? blobGasUsed = null, ulong? excessBlobGas = null, Transaction[]? transactions = null, Hash256? parentBeaconBlockRoot = null) where T : ExecutionPayload, new()
    // {
    //     T blockRequest = new()
    //     {
    //         ParentHash = parent.BlockHash,
    //         FeeRecipient = miner,
    //         StateRoot = parent.StateRoot,
    //         BlockNumber = parent.BlockNumber + 1,
    //         GasLimit = parent.GasLimit,
    //         GasUsed = 0,
    //         ReceiptsRoot = Keccak.EmptyTreeHash,
    //         LogsBloom = Bloom.Empty,
    //         Timestamp = parent.Timestamp + 1,
    //         Withdrawals = withdrawals,
    //         BlobGasUsed = blobGasUsed,
    //         ExcessBlobGas = excessBlobGas,
    //         ParentBeaconBlockRoot = parentBeaconBlockRoot,
    //     };
    //
    //     blockRequest.SetTransactions(transactions ?? Array.Empty<Transaction>());
    //     TryCalculateHash(blockRequest, out Hash256? hash);
    //     blockRequest.BlockHash = hash;
    //     return blockRequest;
    // }
    //
    // private static bool TryCalculateHash(ExecutionPayload request, out Hash256 hash)
    // {
    //     if (request.TryGetBlock(out Block? block) && block is not null)
    //     {
    //         hash = block.CalculateHash();
    //         return true;
    //     }
    //     else
    //     {
    //         hash = Keccak.Zero;
    //         return false;
    //     }
    // }

    private static void WriteJsonRpcRequest(StringBuilder stringBuilder, string methodName, string[]? parameters)
    {
        stringBuilder.Append($"{{\"jsonrpc\":\"2.0\",\"method\":\"{methodName}\",");
        if (parameters is not null) stringBuilder.Append($"\"params\":{parameters},");
        stringBuilder.Append("\"id\":67}");
    }



    // private static string jsonBeginning = "{\"jsonrpc\":\"2.0\",\"method\":\"";
    // private static string jsonEnding = "";

    // private static BlockHeader GetHeader()
    // {
    //     BlockHeader blockHeader = new();
    //
    //     return blockHeader;
    // }

    // private static void ApplyReward(Block block, WorldState stateProvider, IReleaseSpec spec, ISpecProvider specProvider)
    // {
    //     var rewardCalculator = new RewardCalculator(specProvider);
    //     BlockReward[] rewards = rewardCalculator.CalculateRewards(block);
    //
    //     foreach (BlockReward reward in rewards)
    //     {
    //         if (!stateProvider.AccountExists(reward.Address))
    //         {
    //             stateProvider.CreateAccount(reward.Address, reward.Value);
    //         }
    //         else
    //         {
    //             stateProvider.AddToBalance(reward.Address, reward.Value, spec);
    //         }
    //     }
    // }
}
