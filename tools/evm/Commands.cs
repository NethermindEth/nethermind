using System;
using System.IO;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;
namespace Nethermind.Tools.t8n;
using Newtonsoft.Json;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.Core.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.Core;
using Nethermind.Core.Crypto;



using Alloc = Dictionary<String, JsonTypes.Account>;

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

        Alloc allocJson = JsonConvert.DeserializeObject<Alloc>(File.ReadAllText(inputAlloc));
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


        foreach (KeyValuePair<string, JsonTypes.Account> entry in allocJson)
        {
            Console.WriteLine(entry.Key);
            Console.WriteLine(entry.Value.Code);
            Console.WriteLine(entry.Value.Nonce);
            Console.WriteLine(entry.Value.Balance);
            Console.WriteLine(entry.Value.Storage);
        }


        foreach (JsonTypes.Transaction tx in txsJson)
        {
            Console.WriteLine(tx.Nonce);
            Console.WriteLine(tx.Value);
            Console.WriteLine(tx.R);
            Console.WriteLine(tx.S);
            Console.WriteLine(tx.V);
        }



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



        //TODO: Convert to proper classes that Nethermind.Test.Runner uses
        BlockHeader header = new(
    envJson.PreviousHash,
    Keccak.OfAnEmptySequenceRlp,
    envJson.CurrentCoinbase,
    Bytes.FromHexString(envJson.CurrentDifficulty).ToUInt256(),
    envJson.CurrentNumber,
    Bytes.FromHexString(envJson.CurrentGasLimit).ToLongFromBigEndianByteArrayWithoutLeadingZeros(),
    envJson.CurrentTimestamp,
    Array.Empty<byte>());


        return Task.CompletedTask;
    }
}
