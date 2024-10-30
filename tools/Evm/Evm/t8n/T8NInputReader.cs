// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Ethereum.Test.Base;
using Evm.t8n.Errors;
using Evm.t8n.JsonTypes;
using Nethermind.Core;
using Nethermind.Serialization.Json;
using TransactionJson = Evm.t8n.JsonTypes.TransactionJson;

namespace Evm.t8n;

public static class T8NInputReader
{
    private static readonly EthereumJsonSerializer EthereumJsonSerializer = new();
    private const string Stdin = "stdin";

    public static InputData ReadInputData(T8NCommandArguments arguments)
    {
        InputData inputData = new();

        if (arguments.InputAlloc == Stdin || arguments.InputEnv == Stdin || arguments.InputTxs == Stdin)
        {
            inputData = ReadStdInput();
        }

        if (arguments.InputAlloc != Stdin)
        {
            inputData.Alloc = LoadDataFromFile<Dictionary<Address, AccountState>>(arguments.InputAlloc, "alloc");
        }

        if (arguments.InputEnv != Stdin)
        {
            inputData.Env = LoadDataFromFile<EnvJson>(arguments.InputEnv, "env");
        }

        if (arguments.InputTxs != Stdin)
        {
            switch (Path.GetExtension(arguments.InputTxs))
            {
                case ".json":
                    inputData.Txs = LoadDataFromFile<TransactionJson[]>(arguments.InputTxs, "txs");
                    break;
                case ".rlp":
                    inputData.TxRlp = File.ReadAllText(arguments.InputTxs).Replace("\"", "").Replace("\n", "");
                    break;
                default:
                    throw new T8NException("Transactions file support only rlp, json formats", T8NErrorCodes.ErrorIO);
            }
        }

        return inputData;
    }

    private static T LoadDataFromFile<T>(string filePath, string description)
    {
        try
        {
            var fileContent = File.ReadAllText(filePath);
            return EthereumJsonSerializer.Deserialize<T>(fileContent);
        }
        catch (FileNotFoundException)
        {
            throw new T8NException("failed reading {filePath} file: {description}", T8NErrorCodes.ErrorIO);
        }
        catch (JsonException)
        {
            throw new T8NException($"failed unmarshalling {filePath} file: {description}", T8NErrorCodes.ErrorJson);
        }
    }

    private static InputData ReadStdInput()
    {
        using var reader = new StreamReader(Console.OpenStandardInput());
        try
        {
            return EthereumJsonSerializer.Deserialize<InputData>(reader.ReadToEnd());
        }
        catch (Exception e)
        {
            throw new T8NException(e.Message, T8NErrorCodes.ErrorJson);
        }
    }
}
