using System;
using System.IO;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;
namespace Nethermind.Tools.t8n;
using Newtonsoft.Json;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.Logging;
using Nethermind.Core.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Int256;
using Nethermind.Evm.TransactionProcessing;




public class TraceOptions
{
    public bool Memory { get; set; }
    public bool NoMemory { get; set; }
    public bool NoReturnData { get; set; }
    public bool NoStack { get; set; }
    public bool ReturnData { get; set; }
}

public class T8nOutput
{
    public bool Alloc { get; set; }
    public bool Result { get; set; }
    public bool Body { get; set; }
}

public class T8N
{
    public static async Task<int> HandleAsync(
        string? inputAlloc,
        string? inputEnv,
        string? inputTxs,
        string? outputAlloc,
        string? outputBaseDir,
        string? outputBody,
        string? outputResult,
        int stateChainId,
        string? stateFork,
        int stateReward,
        TraceOptions traceOpts
        )
    {
        var t8n = new T8N();
        await t8n.RunAsync(
            inputAlloc,
            inputEnv,
            inputTxs,
            outputAlloc,
            outputBaseDir,
            outputBody,
            outputResult,
            stateChainId,
            stateFork,
            stateReward,
            traceOpts.Memory,
            traceOpts.NoMemory,
            traceOpts.NoReturnData,
            traceOpts.NoStack,
            traceOpts.ReturnData
            );
        return 0;
    }

    public Task RunAsync(
        string? inputAlloc,
        string? inputEnv,
        string? inputTxs,
        string? outputAlloc,
        string? outputBaseDir,
        string? outputBody,
        string? outputResult,
        int stateChainId,
        string? stateFork,
        int stateReward,
        bool traceMemory,
        bool traceNoMemory,
        bool traceNoReturnData,
        bool traceNoStack,
        bool traceReturnData)
    {

        Dictionary<Address, JsonTypes.AccountState> allocJson = JsonConvert.DeserializeObject<Dictionary<Address, JsonTypes.AccountState>>(File.ReadAllText(inputAlloc));
        JsonTypes.Env envJson = JsonConvert.DeserializeObject<JsonTypes.Env>(File.ReadAllText(inputEnv));

        JsonTypes.Transaction[] txsJson = new JsonTypes.Transaction[0];
        //Txs can be passed as json or rlp encoded
        if (inputTxs.EndsWith(".json"))
        {
            txsJson = JsonConvert.DeserializeObject<JsonTypes.Transaction[]>(File.ReadAllText(inputTxs));
        }
        else
        {
            String rlp = File.ReadAllText(inputTxs);

        }

        Console.WriteLine(envJson.CurrentCoinbase);



        //Setup similar to Nethermind.Test.Runner runTest
        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();

        //        TrieStore trieStore = new(stateDb, _logManager);
        //        WorldState stateProvider = new(trieStore, codeDb, _logManager);
        //        IBlockhashProvider blockhashProvider = new TestBlockhashProvider();
        //        IVirtualMachine virtualMachine = new VirtualMachine(
        //            blockhashProvider,
        //            specProvider,
        //            _logManager);
        //



        ILogManager _logManager = LimboLogs.Instance;
        ILogger _logger = _logManager.GetClassLogger();


        //TODO: Refactor ParesSpec
        ISpecProvider specProvider = new CustomSpecProvider(
    ((ForkActivation)0, Frontier.Instance), // TODO: this thing took a lot of time to find after it was removed!, genesis block is always initialized with Frontier
    ((ForkActivation)1, JsonToEthereumTest.ParseSpec(stateFork))); //state.Fork


        TrieStore trieStore = new(stateDb, _logManager);
        WorldState stateProvider = new(trieStore, codeDb, _logManager);
        IBlockhashProvider blockhashProvider = new TestBlockhashProvider();
        IVirtualMachine virtualMachine = new VirtualMachine(
            blockhashProvider,
            specProvider,
            _logManager);

        TransactionProcessor transactionProcessor = new(
            specProvider,
            stateProvider,
            virtualMachine,
            _logManager);



        InitializeFromAlloc(allocJson, stateProvider, specProvider);

        BlockHeader header = new(
            envJson.PreviousHash,
            Keccak.OfAnEmptySequenceRlp,
            envJson.CurrentCoinbase,
            Bytes.FromHexString(envJson.CurrentDifficulty).ToUInt256(),
            envJson.CurrentNumber,
            Bytes.FromHexString(envJson.CurrentGasLimit).ToLongFromBigEndianByteArrayWithoutLeadingZeros(),
            envJson.CurrentTimestamp,
            Array.Empty<byte>()
        );

        if (envJson.CurrentDifficulty is not null)
        {
            header.Difficulty = Bytes.FromHexString(envJson.CurrentDifficulty).ToUInt256();

        }
        TxValidator? txValidator = new((MainnetSpecProvider.Instance.ChainId));
        IReleaseSpec? spec = specProvider.GetSpec((ForkActivation)envJson.CurrentNumber);

        if (envJson.ParentBlobGasUsed is not null && envJson.ParentExcessBlobGas is not null)
        {
            BlockHeader parent = new(
                parentHash: Keccak.Zero,
                unclesHash: Keccak.OfAnEmptySequenceRlp,
                beneficiary: envJson.CurrentCoinbase,
                difficulty: Bytes.FromHexString(envJson.CurrentDifficulty).ToUInt256(),
                number: envJson.CurrentNumber - 1,
                gasLimit: Bytes.FromHexString(envJson.CurrentGasLimit).ToLongFromBigEndianByteArrayWithoutLeadingZeros(),
                timestamp: envJson.CurrentTimestamp,
                extraData: Array.Empty<byte>()
            )
            {
                BlobGasUsed = envJson.ParentBlobGasUsed,
                ExcessBlobGas = envJson.ParentExcessBlobGas
            };
            header.ExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
        }




        //NOTE: This is not working correctly
        foreach (JsonTypes.Transaction jsonTx in txsJson)
        {
            //TODO: Support other than legacy tx and make this a function
            Transaction tx = new Transaction();
            tx.Value = Bytes.FromHexString(jsonTx.Value).ToUInt256();
            tx.Signature = new Signature(Bytes.FromHexString(jsonTx.R), Bytes.FromHexString(jsonTx.S), (ulong)Bytes.FromHexString(jsonTx.V).ToLongFromBigEndianByteArrayWithoutLeadingZeros());
            tx.Data = Bytes.FromHexString(jsonTx.Input);
            tx.To = jsonTx.To;
            tx.Nonce = Bytes.FromHexString(jsonTx.Nonce).ToUInt256();
            tx.GasPrice = Bytes.FromHexString(jsonTx.GasPrice).ToUInt256();
            tx.GasLimit = Bytes.FromHexString(jsonTx.Gas).ToLongFromBigEndianByteArrayWithoutLeadingZeros();

            bool isValid = txValidator.IsWellFormed(tx, spec);
            Console.WriteLine("Tx.IsWellFromed: {0}", isValid);
            if (isValid)
            {
                transactionProcessor.Execute(tx, new BlockExecutionContext(header), NullTxTracer.Instance);
            }
        }
        stateProvider.Commit(specProvider.GenesisSpec);
        stateProvider.CommitTree(1);
        if (!stateProvider.AccountExists(envJson.CurrentCoinbase))
        {
            stateProvider.CreateAccount(envJson.CurrentCoinbase, 0);
        }
        stateProvider.Commit(specProvider.GetSpec((ForkActivation)1));
        stateProvider.RecalculateStateRoot();
        Console.WriteLine(stateProvider.StateRoot);



        return Task.CompletedTask;
    }


    private static void InitializeFromAlloc(Dictionary<Address, JsonTypes.AccountState> alloc, WorldState stateProvider, ISpecProvider specProvider)
    {
        foreach (KeyValuePair<Address, JsonTypes.AccountState> accountState in alloc)
        {
            foreach (KeyValuePair<string, string> storageItem in accountState.Value.Storage)
            {
                UInt256 storageKey = Bytes.FromHexString(storageItem.Key).ToUInt256();
                byte[] storageValue = Bytes.FromHexString(storageItem.Key);
                stateProvider.Set(new StorageCell(accountState.Key, storageKey), storageValue.WithoutLeadingZeros().ToArray());

            }
            stateProvider.CreateAccount(accountState.Key, Bytes.FromHexString(accountState.Value.Balance).ToUInt256(), Bytes.FromHexString(accountState.Value.Nonce).ToUInt256());
            stateProvider.InsertCode(accountState.Key, Bytes.FromHexString(accountState.Value.Code), specProvider.GenesisSpec);
        }
        stateProvider.Commit(specProvider.GenesisSpec);
        stateProvider.CommitTree(0);
        stateProvider.Reset();
    }
}
