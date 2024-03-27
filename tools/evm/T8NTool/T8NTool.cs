// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Ethereum.Test.Base;
using Evm.JsonTypes;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Trie.Pruning;

namespace Evm.T8NTool;

public class T8NTool
{
    private readonly TxDecoder _txDecoder = new();
    private readonly EthereumJsonSerializer _ethereumJsonSerializer = new();
    private readonly LimboLogs _logManager = LimboLogs.Instance;

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
        Dictionary<Address, AccountState> allocJson = _ethereumJsonSerializer.Deserialize<Dictionary<Address, AccountState>>(File.ReadAllText(inputAlloc));
        EnvInfo envInfo = _ethereumJsonSerializer.Deserialize<EnvInfo>(File.ReadAllText(inputEnv));
        Transaction[] transactions;
        if (inputTxs.EndsWith(".json")) {
            TransactionInfo[] txInfoList = _ethereumJsonSerializer.Deserialize<TransactionInfo[]>(File.ReadAllText(inputTxs));
            transactions = txInfoList.Select(txInfo => txInfo.ConvertToTx()).ToArray();
        } else {
            string rlpRaw = File.ReadAllText(inputTxs).Replace("\"", "").Replace("\n", "");
            RlpStream rlp = new(Bytes.FromHexString(rlpRaw));
            transactions = _txDecoder.DecodeArray(rlp).ToArray();
        }

        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();

        IReleaseSpec spec;
        try
        {
            spec = JsonToEthereumTest.ParseSpec(stateFork);
        }
        catch (NotSupportedException e)
        {
            throw new T8NException(e, ExitCodes.ErrorConfig);
        }

        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Frontier.Instance), ((ForkActivation)envInfo.CurrentNumber, spec));

        if (IsPostMerge(spec))
        {
            specProvider.UpdateMergeTransitionInfo(envInfo.CurrentNumber, 0);
        }
        TrieStore trieStore = new(stateDb, _logManager);

        WorldState stateProvider = new(trieStore, codeDb, _logManager);

        var blockhashProvider = new T8NBlockHashProvider();
        IVirtualMachine virtualMachine = new VirtualMachine(
            blockhashProvider,
            specProvider,
            _logManager);

        TransactionProcessor transactionProcessor = new(
            specProvider,
            stateProvider,
            virtualMachine,
            _logManager);

        GeneralStateTestBase.InitializeTestPreState(allocJson, stateProvider, specProvider);

        var ecdsa = new EthereumEcdsa(specProvider.ChainId, _logManager);
        foreach (Transaction transaction in transactions)
        {
            transaction.SenderAddress = ecdsa.RecoverAddress(transaction);
        }

        envInfo.ApplyChecks(specProvider, spec);

        BlockHeader header = envInfo.GetBlockHeader();
        BlockHeader parent = envInfo.GetParentBlockHeader();

        blockhashProvider.Insert(header.Hash, header.Number);
        blockhashProvider.Insert(parent.Hash, parent.Number);
        foreach (KeyValuePair<string, Hash256> envJsonBlockHash in envInfo.BlockHashes)
        {
            blockhashProvider.Insert(envJsonBlockHash.Value, long.Parse(envJsonBlockHash.Key));
        }

        TxValidator txValidator = new(MainnetSpecProvider.Instance.ChainId);
        IReceiptSpec receiptSpec = specProvider.GetSpec(header);
        header.ExcessBlobGas ??= BlobGasCalculator.CalculateExcessBlobGas(parent, spec);

        List<Transaction> successfulTxs = [];
        List<TxReceipt> includedTxReceipts = [];

        BlockHeader[] uncles = envInfo.Ommers
            .Select(ommer => Build.A.BlockHeader
                .WithNumber(envInfo.CurrentNumber - ommer.Delta)
                .WithBeneficiary(ommer.Address)
                .TestObject)
            .ToArray();

        Block block = Build.A.Block.WithHeader(header).WithTransactions(transactions).WithWithdrawals(envInfo.Withdrawals).WithUncles(uncles).TestObject;

        CalculateReward(stateReward, block, stateProvider, spec);

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);
        var withdrawalProcessor = new WithdrawalProcessor(stateProvider, _logManager);
        withdrawalProcessor.ProcessWithdrawals(block, spec);
        stateProvider.Commit(spec);
        stateProvider.RecalculateStateRoot();

        List<RejectedTx> rejectedTxReceipts = [];
        int txIndex = 0;
        foreach (Transaction tx in transactions)
        {
            bool isValid = txValidator.IsWellFormed(tx, spec);
            if (isValid)
            {
                tracer.StartNewTxTrace(tx);
                TransactionResult transactionResult = transactionProcessor.Execute(tx, new BlockExecutionContext(header), tracer);
                tracer.EndTxTrace();

                if (transactionResult.Success)
                {
                    successfulTxs.Add(tx);
                    tracer.LastReceipt.PostTransactionState = null;
                    tracer.LastReceipt.BlockHash = null;
                    tracer.LastReceipt.BlockNumber = 0;
                    includedTxReceipts.Add(tracer.LastReceipt);
                } else if (transactionResult.Error != null)
                {
                    rejectedTxReceipts.Add(new RejectedTx(txIndex, transactionResult.Error));
                    stateProvider.Reset();
                }
                stateProvider.RecalculateStateRoot();
            }

            txIndex++;
        }

        ulong gasUsed = 0;
        if (!tracer.TxReceipts.IsNullOrEmpty())
        {
             gasUsed = (ulong) tracer.LastReceipt.GasUsedTotal;
        }

        Hash256 stateRoot = stateProvider.StateRoot;
        Hash256 txRoot = TxTrie.CalculateRoot(successfulTxs.ToArray());
        Hash256 receiptsRoot = ReceiptTrie<TxReceipt>.CalculateRoot(receiptSpec, includedTxReceipts.ToArray(), ReceiptMessageDecoder.Instance);

        var postState = new PostState
        {
            StateRoot = stateRoot,
            TxRoot = txRoot,
            ReceiptRoot = receiptsRoot,
            Receipts = includedTxReceipts.ToArray(),
            Rejected = rejectedTxReceipts.ToArray(),
            Difficulty = envInfo.CurrentDifficulty,
            GasUsed = new UInt256(gasUsed),
            CurrentBaseFee = envInfo.CurrentBaseFee,
            WithdrawalsRoot = block.WithdrawalsRoot
        };

        var accounts = allocJson.Keys.ToDictionary(address => address, address => stateProvider.GetAccount(address));
        foreach (Ommer ommer in envInfo.Ommers)
        {
            accounts.Add(ommer.Address, stateProvider.GetAccount(ommer.Address));
        }
        if (header.Beneficiary != null)
        {
            accounts.Add(header.Beneficiary, stateProvider.GetAccount(header.Beneficiary));
        }
        var body = Rlp.Encode(successfulTxs.ToArray()).Bytes;

        return new T8NExecutionResult(postState, accounts, body);
    }

    private void WriteToFile(string filename, string? basedir, object outputObject)
    {
        FileInfo fileInfo = new(basedir + filename);
        Directory.CreateDirectory(fileInfo.DirectoryName!);
        using StreamWriter writer = new(fileInfo.FullName);
        writer.Write(_ethereumJsonSerializer.Serialize(outputObject, true));
    }

    private bool IsPostMerge(IReleaseSpec spec)
    {
        return spec == Paris.Instance
               || spec == Shanghai.Instance
               || spec == Cancun.Instance;
    }

    private static void CalculateReward(string? stateReward, Block block, WorldState stateProvider, IReleaseSpec spec)
    {
        if (stateReward == null) return;

        var rewardCalculator = new RewardCalculator(UInt256.Parse(stateReward));
        BlockReward[] rewards = rewardCalculator.CalculateRewards(block);

        foreach (BlockReward blockReward in rewards)
        {
            if (!stateProvider.AccountExists(blockReward.Address))
            {
                stateProvider.CreateAccount(blockReward.Address, blockReward.Value);
            }
            else
            {
                stateProvider.AddToBalance(blockReward.Address, blockReward.Value, spec);
            }
        }
    }
}
