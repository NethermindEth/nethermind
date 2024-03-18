// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Ethereum.Test.Base;
using Evm.JsonTypes;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
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

    public int Execute(
        string inputAlloc,
        string inputEnv,
        string inputTxs,
        string outputAlloc,
        string? outputBasedir,
        string? outputBody,
        string outputResult,
        int stateChainId,
        string stateFork,
        int stateReward,
        bool traceMemory,
        bool traceNoMemory,
        bool traceNoReturnData,
        bool traceNoStack,
        bool traceReturnData)
    {
        try
        {
            T8NExecutionResult t8NExecutionResult = Execute(inputAlloc, inputEnv, inputTxs, stateFork);

            var stdoutObjects = new Dictionary<string, object>();
            OutputObject(outputAlloc, outputBasedir, "alloc", t8NExecutionResult.Alloc, stdoutObjects);
            OutputObject(outputResult, outputBasedir, "result", t8NExecutionResult.PostState, stdoutObjects);
            OutputObject(outputBody, outputBasedir, "body", t8NExecutionResult.Body, stdoutObjects);

            if (!stdoutObjects.IsNullOrEmpty())
            {
                Console.WriteLine(_ethereumJsonSerializer.Serialize(stdoutObjects, true));
            }

            return 0;
        }
        catch (IOException e)
        {
            throw new T8NException(e, ExitCodes.ErrorIO);
        }
        catch (JsonException e)
        {
            throw new T8NException(e, ExitCodes.ErrorJson);
        }
    }

    private T8NExecutionResult Execute(
        string inputAlloc,
        string inputEnv,
        string inputTxs,
        string stateFork)
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

        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Frontier.Instance), ((ForkActivation)envInfo.CurrentNumber, JsonToEthereumTest.ParseSpec(stateFork)));
        TrieStore trieStore = new(stateDb, _logManager);

        WorldState stateProvider = new(trieStore, codeDb, _logManager);
        IReleaseSpec spec = specProvider.GetSpec((ForkActivation)envInfo.CurrentNumber);

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

        BlockHeader header = envInfo.GetBlockHeader();
        BlockHeader parent = envInfo.GetParentBlockHeader();

        envInfo.ApplyChecks(specProvider, spec);

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
        List<TxReceipt> successfulTxReceipts = [];

        Block block = Build.A.Block.WithHeader(header).WithTransactions(transactions).TestObject;

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);

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
                    successfulTxReceipts.Add(tracer.LastReceipt);
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
             gasUsed = (ulong) tracer.LastReceipt.GasUsed;
        }

        Hash256 stateRoot = stateProvider.StateRoot;
        Hash256 txRoot = TxTrie.CalculateRoot(successfulTxs.ToArray());
        Hash256 receiptsRoot = ReceiptTrie<TxReceipt>.CalculateRoot(receiptSpec, successfulTxReceipts.ToArray(), ReceiptMessageDecoder.Instance);

        var postState = new PostState
        {
            StateRoot = stateRoot,
            TxRoot = txRoot,
            ReceiptRoot = receiptsRoot,
            Receipts = successfulTxReceipts.ToArray(),
            Rejected = rejectedTxReceipts.ToArray(),
            Difficulty = header.Difficulty,
            GasUsed = new UInt256(gasUsed)
        };

        var accounts = allocJson.Keys.ToDictionary(address => address, address => stateProvider.GetAccount(address));
        if (header.Beneficiary != null)
        {
            accounts.Add(header.Beneficiary, stateProvider.GetAccount(header.Beneficiary));
        }
        var body = Rlp.Encode(successfulTxs.ToArray()).Bytes;

        return new T8NExecutionResult(postState, accounts, body);
    }

    private void OutputObject(string? filename, string? basedir, string key, object outputObject, IDictionary<string, object> stdoutObjects)
    {
        if (filename == "stdout")
        {
            stdoutObjects.Add(key, outputObject);
        }
        else if (filename != null)
        {
            FileInfo fileInfo = new(basedir + filename);
            Directory.CreateDirectory(fileInfo.DirectoryName!);
            using StreamWriter writer = new(fileInfo.FullName);
            writer.Write(_ethereumJsonSerializer.Serialize(outputObject, true));
        }
    }


}
