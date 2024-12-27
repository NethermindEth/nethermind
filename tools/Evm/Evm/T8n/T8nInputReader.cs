// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Ethereum.Test.Base;
using Evm.T8n.Errors;
using Evm.T8n.JsonTypes;
using Nethermind.Core;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;

namespace Evm.T8n;

public static class T8nInputReader
{
    private static readonly EthereumJsonSerializer EthereumJsonSerializer = new();
    private const string Stdin = "stdin";

    public static InputData ReadInputData(T8nCommandArguments arguments)
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
                    inputData.Txs = LoadDataFromFile<TransactionForRpc[]>(arguments.InputTxs, "txs");
                    inputData.TransactionMetaDataList = LoadDataFromFile<TransactionMetaData[]>(arguments.InputTxs, "txs");
                    break;
                case ".rlp":
                    inputData.TxRlp = File.ReadAllText(arguments.InputTxs).Replace("\"", "").Replace("\n", "");
                    break;
                default:
                    throw new T8nException("Transactions file support only rlp, json formats", T8nErrorCodes.ErrorIO);
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
        catch (FileNotFoundException e)
        {
            throw new T8nException(e, "failed reading {filePath} file: {description}", T8nErrorCodes.ErrorIO);
        }
        catch (JsonException e)
        {
            throw new T8nException(e, $"failed unmarshalling {filePath} file: {description}", T8nErrorCodes.ErrorJson);
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
            throw new T8nException(e, T8nErrorCodes.ErrorJson);
        }
    }
}
