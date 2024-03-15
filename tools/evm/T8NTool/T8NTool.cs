// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Evm.JsonTypes;
using Microsoft.IdentityModel.Tokens;
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

    public T8NExecutionResult Execute(
        string inputAlloc,
        string inputEnv,
        string inputTxs,
        string outputAlloc,
        string? outputBaseDir,
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
        Dictionary<Address, AccountState> allocJson = _ethereumJsonSerializer.Deserialize<Dictionary<Address, AccountState>>(File.ReadAllText(inputAlloc));
        EnvInfo envInfo = _ethereumJsonSerializer.Deserialize<EnvInfo>(File.ReadAllText(inputEnv));
        List<Transaction> transactions;
        if (inputTxs.EndsWith(".json")) {
            TransactionInfo[] txInfoList = _ethereumJsonSerializer.Deserialize<TransactionInfo[]>(File.ReadAllText(inputTxs));
            transactions = txInfoList.Select(txInfo => txInfo.ConvertToTx()).ToList();
        } else {
            string rlpRaw = File.ReadAllText(inputTxs).Replace("\"", "").Replace("\n", "");
            RlpStream rlp = new(Bytes.FromHexString(rlpRaw));
            transactions = _txDecoder.DecodeArray(rlp).ToList();
        }

        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();

        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Frontier.Instance), ((ForkActivation)1, JsonToEthereumTest.ParseSpec(stateFork)));
        TrieStore trieStore = new(stateDb, _logManager);

        WorldState stateProvider = new(trieStore, codeDb, _logManager);
        var blockhashProvider = new TestBlockhashProvider();
        IVirtualMachine virtualMachine = new VirtualMachine(
            blockhashProvider,
            specProvider,
            _logManager);

        TransactionProcessor transactionProcessor = new(
            specProvider,
            stateProvider,
            virtualMachine,
            _logManager);

        InitializeFromAlloc(allocJson, stateProvider, specProvider);

        IEthereumEcdsa ecdsa = new EthereumEcdsa(specProvider.ChainId, _logManager);
        foreach (Transaction transaction in transactions)
        {
            transaction.SenderAddress = ecdsa.RecoverAddress(transaction);
        }

        BlockHeader header = envInfo.GetBlockHeader();
        blockhashProvider.Insert(header.Hash, header.Number);

        foreach (KeyValuePair<string, Hash256> envJsonBlockHash in envInfo.BlockHashes)
        {
            blockhashProvider.Insert(envJsonBlockHash.Value, long.Parse(envJsonBlockHash.Key));
        }

        TxValidator txValidator = new(MainnetSpecProvider.Instance.ChainId);
        IReleaseSpec spec = specProvider.GetSpec((ForkActivation)envInfo.CurrentNumber);
        IReceiptSpec receiptSpec = specProvider.GetSpec(header);
        BlockHeader parent = envInfo.GetParentBlockHeader();
        header.ExcessBlobGas ??= BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
        if (header.BaseFeePerGas.IsZero)
        {
            if (spec.IsEip1559Enabled && parent.BaseFeePerGas.IsZero)
            {
                throw new Exception("EIP-1559 config but missing 'currentBaseFee' in env section");
            }
            header.BaseFeePerGas = BaseFeeCalculator.Calculate(parent, spec);
        }
        blockhashProvider.Insert(parent.Hash, parent.Number);

        List<Transaction> successfulTxs = [];
        List<TxReceipt> successfulTxReceipts = [];

        Block block = Build.A.Block.WithHeader(header).WithTransactions(transactions.ToArray()).TestObject;

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

        var t8NOutput = new T8NExecutionResult(postState, accounts);

        Console.WriteLine(_ethereumJsonSerializer.Serialize(t8NOutput, true));

        return t8NOutput;
    }

    private static void InitializeFromAlloc(Dictionary<Address, AccountState> alloc, WorldState stateProvider, ISpecProvider specProvider)
    {
        foreach ((Address address, AccountState accountState) in alloc)
        {
            foreach (KeyValuePair<UInt256, byte[]> storageItem in accountState.Storage)
            {
                stateProvider.Set(new StorageCell(address, storageItem.Key), storageItem.Value);
            }
            stateProvider.CreateAccount(address, accountState.Balance, accountState.Nonce);
            stateProvider.InsertCode(address, accountState.Code, specProvider.GenesisSpec);
        }

        stateProvider.Commit(specProvider.GenesisSpec);
        stateProvider.CommitTree(0);
        stateProvider.Reset();
    }
}
