using Ethereum.Test.Base;
using Evm.JsonTypes;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;

namespace Evm.T8NTool;

public class InputProcessor
{
    private static readonly EthereumJsonSerializer EthereumJsonSerializer = new();
    private static readonly TxDecoder TxDecoder = new();

    public static GeneralStateTest Convert(string inputAlloc,
        string inputEnv,
        string inputTxs,
        string stateFork,
        string? stateReward)
    {
        Dictionary<Address, AccountState> allocJson = EthereumJsonSerializer.Deserialize<Dictionary<Address, AccountState>>(File.ReadAllText(inputAlloc));
        EnvInfo envInfo = EthereumJsonSerializer.Deserialize<EnvInfo>(File.ReadAllText(inputEnv));

        Transaction[] transactions;
        var txFileContent = File.ReadAllText(inputTxs);
        if (inputTxs.EndsWith(".json")) {
            var txInfoList = EthereumJsonSerializer.Deserialize<TransactionInfo[]>(txFileContent);
            transactions = txInfoList.Select(txInfo => txInfo.ConvertToTx()).ToArray();
        } else if (inputTxs.EndsWith(".rlp")) {
            string rlpRaw = txFileContent.Replace("\"", "").Replace("\n", "");
            RlpStream rlp = new(Bytes.FromHexString(rlpRaw));
            transactions = TxDecoder.DecodeArray(rlp);
        }
        else
        {
            throw new NotSupportedException("Transactions file support only rlp, json formats");
        }
        
        IReleaseSpec spec = JsonToEthereumTest.ParseSpec(stateFork);

        GeneralStateTest generalStateTest = new();
        generalStateTest.Fork = spec;
        generalStateTest.Pre = allocJson;
        generalStateTest.Transactions = transactions;

        generalStateTest.CurrentCoinbase = envInfo.CurrentCoinbase;
        generalStateTest.CurrentGasLimit = envInfo.CurrentGasLimit;
        generalStateTest.CurrentTimestamp = envInfo.CurrentTimestamp;
        generalStateTest.CurrentNumber = envInfo.CurrentNumber;
        generalStateTest.Withdrawals = envInfo.Withdrawals;
        generalStateTest.Withdrawals = envInfo.Withdrawals;
        generalStateTest.CurrentRandom = envInfo.GetCurrentRandomHash256();
        generalStateTest.ParentTimestamp = envInfo.ParentTimestamp;
        generalStateTest.ParentDifficulty = envInfo.ParentDifficulty;
        generalStateTest.CurrentBaseFee = envInfo.CurrentBaseFee;
        // generalStateTest.CurrentDifficulty = envInfo.CalculateCurrentDifficultyWithMergeChecks(specProvider);
        generalStateTest.ParentUncleHash = envInfo.ParentUncleHash;
        generalStateTest.ParentBeaconBlockRoot = envInfo.ParentBeaconBlockRoot;
        generalStateTest.ParentBaseFee = envInfo.ParentBaseFee;
        generalStateTest.ParentGasUsed = envInfo.ParentGasUsed;
        generalStateTest.ParentGasLimit = envInfo.ParentGasLimit;
        generalStateTest.ParentExcessBlobGas = envInfo.ParentExcessBlobGas;
        generalStateTest.CurrentExcessBlobGas = envInfo.CurrentExcessBlobGas;
        generalStateTest.ParentBlobGasUsed = envInfo.ParentBlobGasUsed;

        return generalStateTest;
    }
}