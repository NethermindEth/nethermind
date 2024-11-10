// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Evm.T8n.Errors;
using Evm.T8n.JsonConverters;
using Evm.T8n.JsonTypes;
using Nethermind.JsonRpc.Converters;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Evm.T8n;

public static class T8nTool
{
    private static readonly EthereumJsonSerializer _ethereumJsonSerializer = new();
    private const string Stdout = "stdout";

    static T8nTool()
    {
        EthereumJsonSerializer.AddConverter(new TxReceiptConverter());
        EthereumJsonSerializer.AddConverter(new AccountStateJsonConverter());
    }

    public static T8nOutput Run(T8nCommandArguments arguments, ILogManager logManager)
    {
        ILogger logger = logManager.GetClassLogger();
        T8nOutput t8nOutput = new();
        try
        {
            T8nExecutionResult t8nExecutionResult = T8nExecutor.Execute(arguments);

            t8nOutput.Alloc = GetOrWriteToFile(t8nExecutionResult.Accounts, arguments.OutputAlloc, arguments.OutputBaseDir);
            t8nOutput.Result = GetOrWriteToFile(t8nExecutionResult.PostState, arguments.OutputResult, arguments.OutputBaseDir);
            t8nOutput.Body = GetOrWriteToFile(t8nExecutionResult.TransactionsRlp, arguments.OutputBody, arguments.OutputBaseDir);

            if (!t8nOutput.IsEmpty())
            {
                Console.WriteLine(_ethereumJsonSerializer.Serialize(t8nOutput, true));
            }
        }
        catch (T8nException e)
        {
            t8nOutput = new T8nOutput(e.Message, e.ExitCode);
            logger.Error(e.Message, e);
        }
        catch (IOException e)
        {
            t8nOutput = new T8nOutput(e.Message, T8nErrorCodes.ErrorIO);
            logger.Error(e.Message, e);
        }
        catch (JsonException e)
        {
            t8nOutput = new T8nOutput(e.Message, T8nErrorCodes.ErrorJson);
            logger.Error(e.Message, e);
        }
        catch (Exception e)
        {
            t8nOutput = new T8nOutput(e.Message, T8nErrorCodes.ErrorEvm);
            logger.Error(e.Message, e);
        }
        finally
        {
            if (t8nOutput.ErrorMessage is not null)
            {
                Console.WriteLine(t8nOutput.ErrorMessage);
            }
        }

        return t8nOutput;
    }

    private static T? GetOrWriteToFile<T>(T t8nResultObject, string? outputFile, string outputBasedir)
    {
        if (outputFile == Stdout) return t8nResultObject;
        if (outputFile is not null) WriteToFile(outputFile, outputBasedir, t8nResultObject);

        return default;
    }

    private static void WriteToFile<T>(string filename, string basedir, T outputObject)
    {
        FileInfo fileInfo = new(Path.Combine(basedir, filename));
        Directory.CreateDirectory(fileInfo.DirectoryName!);
        using StreamWriter writer = new(fileInfo.FullName);
        writer.Write(_ethereumJsonSerializer.Serialize(outputObject, true));
    }
}
