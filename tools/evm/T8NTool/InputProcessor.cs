using Ethereum.Test.Base;
using Evm.JsonTypes;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;

namespace Evm.T8NTool;

public class InputProcessor
{
    private static readonly EthereumJsonSerializer EthereumJsonSerializer = new();
    private static readonly TxDecoder TxDecoder = TxDecoder.Instance;

    public static T8nTestCase ConvertToGeneralStateTest(string inputAlloc,
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
        Dictionary<Address, AccountState> allocJson =
            EthereumJsonSerializer.Deserialize<Dictionary<Address, AccountState>>(File.ReadAllText(inputAlloc));
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

        ISpecProvider specProvider = stateChainId == GnosisSpecProvider.Instance.ChainId ? GnosisSpecProvider.Instance
            : new CustomSpecProvider(((ForkActivation)0, Frontier.Instance), ((ForkActivation)1, spec));

        if (spec is Paris)
        {
            specProvider.UpdateMergeTransitionInfo(envInfo.CurrentNumber, 0);
        }

        envInfo.ApplyChecks(specProvider, spec);

        var generalStateTest = new T8nTestCase
        {
            Fork = spec,
            SpecProvider = specProvider,
            Pre = allocJson,
            Transactions = transactions,
            CurrentCoinbase = envInfo.CurrentCoinbase,
            CurrentGasLimit = envInfo.CurrentGasLimit,
            CurrentTimestamp = envInfo.CurrentTimestamp,
            CurrentNumber = envInfo.CurrentNumber,
            Withdrawals = envInfo.Withdrawals,
            CurrentRandom = envInfo.GetCurrentRandomHash256(),
            ParentTimestamp = envInfo.ParentTimestamp,
            ParentDifficulty = envInfo.ParentDifficulty,
            CurrentBaseFee = envInfo.CurrentBaseFee,
            CurrentDifficulty = envInfo.CurrentDifficulty,
            ParentUncleHash = envInfo.ParentUncleHash,
            ParentBaseFee = envInfo.ParentBaseFee,
            ParentBeaconBlockRoot = envInfo.ParentBeaconBlockRoot,
            ParentGasUsed = envInfo.ParentGasUsed,
            ParentGasLimit = envInfo.ParentGasLimit,
            ParentExcessBlobGas = envInfo.ParentExcessBlobGas,
            CurrentExcessBlobGas = envInfo.CurrentExcessBlobGas,
            ParentBlobGasUsed = envInfo.ParentBlobGasUsed,
            Ommers = envInfo.Ommers,
            StateReward = stateReward,
            BlockHashes = envInfo.BlockHashes,
            StateChainId = stateChainId,
            GethTraceOptions = gethTraceOptions,
        };


        return generalStateTest;
    }
}
