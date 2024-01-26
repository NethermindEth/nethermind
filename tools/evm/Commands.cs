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


        //TODO: Parse state.fork and select the proper spec here
        ISpecProvider specProvider = new CustomSpecProvider(
    ((ForkActivation)0, Frontier.Instance), // TODO: this thing took a lot of time to find after it was removed!, genesis block is always initialized with Frontier
    ((ForkActivation)1, Byzantium.Instance)); //state.Fork


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
    Array.Empty<byte>());


        //    for(JsonTypes.Transaction tx in txsJson)
        //        {
        //            bool isValid = txValidator.IsWellFormed(tx, spec);
        //            if (isValid)
        //            {
        //                transactionProcessor.Execute(test.Transaction, new BlockExecutionContext(header), txTracer);
        //
        //            }
        //
        //        }
        //
        Console.WriteLine(stateProvider);


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
