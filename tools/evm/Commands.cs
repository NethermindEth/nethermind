using System;
using System.IO;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;
namespace Nethermind.Tools.t8n;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

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

        Dictionary<String, JsonTypes.Alloc> allocJson = JsonConvert.DeserializeObject<Dictionary<String, JsonTypes.Alloc>>(File.ReadAllText(inputAlloc));
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


        foreach (KeyValuePair<string, JsonTypes.Alloc> entry in allocJson)
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

        //TODO: Convert to proper classes that Nethermind.Test.Runner uses


        return Task.CompletedTask;
    }
}
