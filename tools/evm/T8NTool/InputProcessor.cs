using Ethereum.Test.Base;
using Ethereum.Test.Base.T8NUtils;
using Evm.JsonTypes;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;

namespace Evm.T8NTool;

public class InputProcessor
{
    private static readonly EthereumJsonSerializer EthereumJsonSerializer = new();
    private static readonly TxDecoder TxDecoder = new();

    public static GeneralStateTest ConvertToGeneralStateTest(string inputAlloc,
        string inputEnv,
        string inputTxs,
        string stateFork,
        string? stateReward,
        ulong stateChainId,
        TraceOptions traceOptions)
    {
        GethTraceOptions gethTraceOptions = new GethTraceOptions
        {
            EnableMemory = traceOptions.Memory,
            DisableStack = traceOptions.NoStack
        };
        Dictionary<Address, AccountState> allocJson = EthereumJsonSerializer.Deserialize<Dictionary<Address, AccountState>>(File.ReadAllText(inputAlloc));
        EnvInfo envInfo = EthereumJsonSerializer.Deserialize<EnvInfo>(File.ReadAllText(inputEnv));

        Transaction[] transactions;
        var txFileContent = File.ReadAllText(inputTxs);
        if (inputTxs.EndsWith(".json"))
        {
            var txInfoList = EthereumJsonSerializer.Deserialize<TransactionInfo[]>(txFileContent);
            transactions = txInfoList.Select(txInfo => txInfo.ConvertToTx()).ToArray();
        }
        else if (inputTxs.EndsWith(".rlp"))
        {
            string rlpRaw = txFileContent.Replace("\"", "").Replace("\n", "");
            RlpStream rlp = new(Bytes.FromHexString(rlpRaw));
            transactions = TxDecoder.DecodeArray(rlp);
        }
        else
        {
            throw new T8NException("Transactions file support only rlp, json formats", ExitCodes.ErrorConfig);
        }

        IReleaseSpec spec;
        try
        {
            spec = JsonToEthereumTest.ParseSpec(stateFork);
        }
        catch (NotSupportedException e)
        {
            throw new T8NException(e, ExitCodes.ErrorConfig);
        }

        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Frontier.Instance), ((ForkActivation)1, spec));
        if (spec is Paris)
        {
            specProvider.UpdateMergeTransitionInfo(envInfo.CurrentNumber, 0);
        }
        envInfo.ApplyChecks(specProvider, spec);

        GeneralStateTest generalStateTest = new();
        generalStateTest.IsT8NTest = true;
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
        generalStateTest.CurrentDifficulty = envInfo.CurrentDifficulty;
        generalStateTest.ParentUncleHash = envInfo.ParentUncleHash;
        generalStateTest.ParentBeaconBlockRoot = envInfo.ParentBeaconBlockRoot;
        generalStateTest.ParentBaseFee = envInfo.ParentBaseFee;
        generalStateTest.ParentGasUsed = envInfo.ParentGasUsed;
        generalStateTest.ParentGasLimit = envInfo.ParentGasLimit;
        generalStateTest.ParentExcessBlobGas = envInfo.ParentExcessBlobGas;
        generalStateTest.CurrentExcessBlobGas = envInfo.CurrentExcessBlobGas;
        generalStateTest.ParentBlobGasUsed = envInfo.ParentBlobGasUsed;
        generalStateTest.Ommers = envInfo.Ommers;
        generalStateTest.StateReward = stateReward;
        generalStateTest.BlockHashes = envInfo.BlockHashes;
        generalStateTest.StateChainId = stateChainId;
        generalStateTest.GethTraceOptions = gethTraceOptions;

        return generalStateTest;
    }
}
