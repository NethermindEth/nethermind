using System.Text.Json;
using Ethereum.Test.Base;
using Evm.JsonTypes;
using Nethermind.Evm.Tracing.GethStyle.Custom;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;

namespace Evm.T8NTool;

public class Tool : GeneralStateTestBase
{
    private readonly EthereumJsonSerializer _ethereumJsonSerializer;

    public Tool()
    {
        _ethereumJsonSerializer = new EthereumJsonSerializer();
        EthereumJsonSerializer.AddConverter(new ReceiptJsonConverter());
    }

    public T8NOutput Execute(
        string inputAlloc,
        string inputEnv,
        string inputTxs,
        string? outputBasedir,
        string? outputAlloc,
        string? outputBody,
        string? outputResult,
        int stateChainId,
        string stateFork,
        string? stateReward,
        bool traceMemory,
        bool traceNoMemory,
        bool traceNoReturnData,
        bool traceNoStack,
        bool traceReturnData)
    {
        T8NOutput t8NOutput = new();
        try
        {
            var t8NExecutionResult = Execute(inputAlloc, inputEnv, inputTxs, stateFork, stateReward);

            if (outputAlloc == "stdout") t8NOutput.Alloc = t8NExecutionResult.Alloc;
            else if (outputAlloc != null) WriteToFile(outputAlloc, outputBasedir, t8NExecutionResult.Alloc);

            if (outputResult == "stdout") t8NOutput.Result = t8NExecutionResult.PostState;
            else if (outputResult != null) WriteToFile(outputResult, outputBasedir, t8NExecutionResult.PostState);
            
            if (outputBody == "stdout") t8NOutput.Body = t8NExecutionResult.Body;
            else if (outputBody != null) WriteToFile(outputBody, outputBasedir, t8NExecutionResult.Body);

            if (t8NOutput.Body != null || t8NOutput.Alloc != null || t8NOutput.Result != null)
            {
                Console.WriteLine(_ethereumJsonSerializer.Serialize(t8NOutput, true));
            }
        }
        catch (T8NException e)
        {
            t8NOutput = new T8NOutput(e.Message, e.ExitCode);
        }
        catch (IOException e)
        {
            t8NOutput = new T8NOutput(e.Message, ExitCodes.ErrorIO);
        }
        catch (JsonException e)
        {
            t8NOutput = new T8NOutput(e.Message, ExitCodes.ErrorJson);
        }
        catch (Exception e)
        {
            t8NOutput = new T8NOutput(e.Message, ExitCodes.ErrorEVM);
        }
        finally
        {
            if (t8NOutput.ErrorMessage != null)
            {
                Console.WriteLine(t8NOutput.ErrorMessage);
            }
        }
        return t8NOutput;
    }

    private T8NExecutionResult Execute(
        string inputAlloc,
        string inputEnv,
        string inputTxs,
        string stateFork,
        string? stateReward)
    {
        var generalStateTest = InputProcessor.Convert(inputAlloc, inputEnv, inputTxs, stateFork, stateReward);

        generalStateTest.Name = "T8N";
        var res = RunTest(generalStateTest, new T8NToolTracer());

        PostState postState = new PostState();
        postState.StateRoot = res.StateRoot;
        postState.TxRoot = res.TxRoot;
        postState.ReceiptsRoot = res.ReceiptsRoot;
        postState.WithdrawalsRoot = res.WithdrawalsRoot;
        postState.LogsHash = res.LogsHash;
        postState.LogsBloom = res.LogsBloom;
        postState.Receipts = res.Receipts;
        postState.Rejected = res.Rejected;
        postState.CurrentDifficulty = res.CurrentDifficulty;
        postState.GasUsed = res.GasUsed;
        postState.CurrentBaseFee = res.CurrentBaseFee;
        postState.CurrentExcessBlobGas = res.CurrentExcessBlobGas;
        postState.BlobGasUsed = res.BlobGasUsed;

        return new T8NExecutionResult(postState, res.Accounts, res.TransactionsRlp);
    }

    private void WriteToFile(string filename, string? basedir, object outputObject)
    {
        FileInfo fileInfo = new(basedir + filename);
        Directory.CreateDirectory(fileInfo.DirectoryName!);
        using StreamWriter writer = new(fileInfo.FullName);
        writer.Write(_ethereumJsonSerializer.Serialize(outputObject, true));
    }
}