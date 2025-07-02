// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.Text.Json;
using Evm.T8n.Errors;
using Evm.T8n.JsonConverters;
using Evm.T8n.JsonTypes;
using Nethermind.JsonRpc.Converters;
using Nethermind.Logging;
using Nethermind.Logging.NLog;
using Nethermind.Serialization.Json;

namespace Evm.T8n;

public static class T8nCommand
{
    private static readonly EthereumJsonSerializer _ethereumJsonSerializer = new();
    private const string Stdout = "stdout";
    private static ILogManager _logManager = new NLogManager("t8n.log");
    private static ILogger _logger = _logManager.GetClassLogger();

    static T8nCommand()
    {
        EthereumJsonSerializer.AddConverter(new TxReceiptConverter());
        EthereumJsonSerializer.AddConverter(new AccountStateJsonConverter());
    }

    public static void Configure(ref RootCommand rootCmd)
    {
        Command cmd = T8nCommandOptions.CreateCommand();

        cmd.SetAction(parseResult =>
        {
            var arguments = T8nCommandArguments.FromParseResult(parseResult);

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
                _logger.Error(e.Message, e);
            }
            catch (IOException e)
            {
                t8nOutput = new T8nOutput(e.Message, T8nErrorCodes.ErrorIO);
                _logger.Error(e.Message, e);
            }
            catch (JsonException e)
            {
                t8nOutput = new T8nOutput(e.Message, T8nErrorCodes.ErrorJson);
                _logger.Error(e.Message, e);
            }
            catch (Exception e)
            {
                t8nOutput = new T8nOutput(e.Message, T8nErrorCodes.ErrorEvm);
                _logger.Error(e.Message, e);
            }
            finally
            {
                Environment.ExitCode = t8nOutput.ExitCode;
                if (t8nOutput.ErrorMessage is not null)
                {
                    Console.WriteLine(t8nOutput.ErrorMessage);
                }
            }
        });

        rootCmd.Add(cmd);
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
