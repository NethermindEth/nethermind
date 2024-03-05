using JsonTypes;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Json;
using Nethermind.State.Proofs;

namespace Nethermind.Tools.t8n;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ethereum.Test.Base;
using Newtonsoft.Json;
using Nethermind.Db;
using Nethermind.Specs;
using Nethermind.Logging;
using Nethermind.Core.Extensions;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Int256;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Rlp;




public class TraceOptions
{
    public bool Memory { get; set; }
    public bool NoMemory { get; set; }
    public bool NoReturnData { get; set; }
    public bool NoStack { get; set; }
    public bool ReturnData { get; set; }
}

public class T8nOutput
{
    public bool Alloc { get; set; }
    public bool Result { get; set; }
    public bool Body { get; set; }
}

public class T8N
{
    private TxDecoder _txDecoder = new();
    private EthereumJsonSerializer _ethereumJsonSerializer = new();

    public static async Task<int> HandleAsync(
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
        TraceOptions traceOpts
        )
    {
        var t8n = new T8N();
        await t8n.RunAsync(
            inputAlloc,
            inputEnv,
            inputTxs,
            outputAlloc,
            outputBaseDir,
            outputBody,
            outputResult,
            stateChainId,
            stateFork,
            stateReward,
            traceOpts.Memory,
            traceOpts.NoMemory,
            traceOpts.NoReturnData,
            traceOpts.NoStack,
            traceOpts.ReturnData
            );
        return 0;
    }

    public Task RunAsync(
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
        Dictionary<Address, JsonTypes.AccountState> allocJson = _ethereumJsonSerializer.Deserialize<Dictionary<Address, JsonTypes.AccountState>>(File.ReadAllText(inputAlloc));
        Env envJson = _ethereumJsonSerializer.Deserialize<Env>(File.ReadAllText(inputEnv));

        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();

        ILogManager _logManager = LimboLogs.Instance;
        ILogger _logger = _logManager.GetClassLogger();

        ISpecProvider specProvider = new CustomSpecProvider(
    ((ForkActivation)0, Frontier.Instance), // TODO: this thing took a lot of time to find after it was removed!, genesis block is always initialized with Frontier
    ((ForkActivation)1, JsonToEthereumTest.ParseSpec(stateFork)));
        TrieStore trieStore = new(stateDb, _logManager);

        WorldState stateProvider = new(trieStore, codeDb, _logManager);
        IBlockhashProvider blockhashProvider = new TestBlockhashProvider();
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

        List<Transaction> transactions = new List<Transaction>();
        if (inputTxs.EndsWith(".json")) {
            JsonTypes.Transaction[] txsJson = JsonConvert.DeserializeObject<JsonTypes.Transaction[]>(File.ReadAllText(inputTxs));
            foreach (JsonTypes.Transaction jsonTx in txsJson)
            {
                transactions.Add(convertToTx(jsonTx));
            }
        } else {
            String rlpRaw = File.ReadAllText(inputTxs).Replace("\"", "").Replace("\n", "");
            RlpStream rlp = new(Bytes.FromHexString(rlpRaw));
            transactions = _txDecoder.DecodeArray(rlp).ToList();
        }

        IEthereumEcdsa ecdsa = new EthereumEcdsa(specProvider.ChainId, _logManager);
        foreach (Transaction transaction in transactions)
        {
            transaction.SenderAddress = ecdsa.RecoverAddress(transaction);
        }

        BlockHeader header = new(
            envJson.PreviousHash,
            Keccak.OfAnEmptySequenceRlp,
            envJson.CurrentCoinbase,
            Bytes.FromHexString(envJson.CurrentDifficulty).ToUInt256(),
            envJson.CurrentNumber,
            Bytes.FromHexString(envJson.CurrentGasLimit).ToLongFromBigEndianByteArrayWithoutLeadingZeros(),
            envJson.CurrentTimestamp,
            Array.Empty<byte>()
        );

        TxValidator? txValidator = new((MainnetSpecProvider.Instance.ChainId));
        IReleaseSpec? spec = specProvider.GetSpec((ForkActivation)envJson.CurrentNumber);
        IReceiptSpec? receiptSpec = specProvider.GetSpec(header);
        if (envJson.ParentBlobGasUsed is not null && envJson.ParentExcessBlobGas is not null)
        {
            BlockHeader parent = new(
                parentHash: Keccak.Zero,
                unclesHash: Keccak.OfAnEmptySequenceRlp,
                beneficiary: envJson.CurrentCoinbase,
                difficulty: Bytes.FromHexString(envJson.CurrentDifficulty).ToUInt256(),
                number: envJson.CurrentNumber - 1,
                gasLimit: Bytes.FromHexString(envJson.CurrentGasLimit).ToLongFromBigEndianByteArrayWithoutLeadingZeros(),
                timestamp: envJson.CurrentTimestamp,
                extraData: Array.Empty<byte>()
            )
            {
                BlobGasUsed = envJson.ParentBlobGasUsed,
                ExcessBlobGas = envJson.ParentExcessBlobGas
            };
            header.ExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parent, spec);
        }

        List<Transaction> successfulTxs = new List<Transaction>();
        List<TxReceipt> successfulTxReceipts = new List<TxReceipt>();
        List<TxReceipt> rejectedTxReceipts = new List<TxReceipt>();

        Block block = Build.A.Block.WithHeader(header).WithTransactions(transactions.ToArray()).TestObject;

        BlockReceiptsTracer tracer = new();
        tracer.StartNewBlockTrace(block);

        foreach (Transaction tx in transactions)
        {
            bool isValid = txValidator.IsWellFormed(tx, spec);
            if (isValid)
            {
                tracer.StartNewTxTrace(tx);
                transactionProcessor.Execute(tx, new BlockExecutionContext(header), tracer);
                tracer.EndTxTrace();

                if (tracer.LastReceipt.StatusCode == 1)
                {
                    successfulTxs.Add(tx);
                    tracer.LastReceipt.PostTransactionState = null;
                    tracer.LastReceipt.BlockHash = null;
                    tracer.LastReceipt.BlockNumber = 0;
                    successfulTxReceipts.Add(tracer.LastReceipt);
                } else if (tracer.LastReceipt.StatusCode == 0)
                {
                    rejectedTxReceipts.Add(tracer.LastReceipt);
                    stateProvider.Reset();
                }
                stateProvider.RecalculateStateRoot();
            }
        }

        ulong gasUsed = 0;
        if (!tracer.TxReceipts.IsNullOrEmpty())
        {
             gasUsed = (ulong) tracer.LastReceipt.GasUsed;
        }

        var stateRoot = stateProvider.StateRoot;
        var txRoot = TxTrie.CalculateRoot(successfulTxs.ToArray());
        var receiptsRoot = ReceiptTrie.CalculateRoot(receiptSpec, successfulTxReceipts.ToArray());

        ExecutionResult executionResult = new ExecutionResult();
        executionResult.StateRoot = stateRoot;
        executionResult.TxRoot = txRoot;
        executionResult.ReceiptRoot = receiptsRoot;
        executionResult.Receipts = successfulTxReceipts.ToArray();
        executionResult.Rejected = rejectedTxReceipts.Select(rejectedTx => new RejectedTx(rejectedTx.Index, rejectedTx.Error)).ToArray();
        executionResult.Difficulty = header.Difficulty;
        executionResult.GasUsed = new UInt256(gasUsed);

        string json = _ethereumJsonSerializer.Serialize(executionResult, true);
        Console.WriteLine(json);

        Dictionary<Address, Account> accounts = new Dictionary<Address, Account>();
        foreach (var address in allocJson.Keys)
        {
            accounts.Add(address, stateProvider.GetAccount(address));
        }

        accounts.Add(header.Beneficiary, stateProvider.GetAccount(header.Beneficiary));
        string accountsJson = new EthereumJsonSerializer().Serialize(accounts, true);

        Console.WriteLine(accountsJson);

        return Task.CompletedTask;
    }

    private static Transaction convertToTx(JsonTypes.Transaction jsonTx)
    {
        Transaction tx = new Transaction();
        tx.Value = Bytes.FromHexString(jsonTx.Value).ToUInt256();
        tx.Signature = new Signature(Bytes.FromHexString(jsonTx.R), Bytes.FromHexString(jsonTx.S), (ulong)Bytes.FromHexString(jsonTx.V).ToLongFromBigEndianByteArrayWithoutLeadingZeros());
        tx.Data = Bytes.FromHexString(jsonTx.Input);
        tx.To = jsonTx.To;
        tx.Nonce = Bytes.FromHexString(jsonTx.Nonce).ToUInt256();
        tx.GasLimit = Bytes.FromHexString(jsonTx.Gas).ToLongFromBigEndianByteArrayWithoutLeadingZeros();
        tx.Hash = new Hash256(Bytes.FromHexString(jsonTx.Hash));
        //Legacy doesn't need type
        if (jsonTx.Type is null || jsonTx.Type == "0x0")
        {
            tx.Type = TxType.Legacy;
            tx.GasPrice = Bytes.FromHexString(jsonTx.GasPrice).ToUInt256();
        }
        else if (jsonTx.Type == "0x1")
        {
            tx.Type = TxType.AccessList;
            tx.AccessList = jsonTx.AccessList;
        }
        else if (jsonTx.Type == "0x2")
        {
            tx.Type = TxType.EIP1559;
            tx.DecodedMaxFeePerGas = Bytes.FromHexString(jsonTx.MaxFeePerGas).ToUInt256();
            tx.GasPrice = Bytes.FromHexString(jsonTx.MaxPriorityFeePerGas).ToUInt256();
        }
        else if (jsonTx.Type == "0x3")
        {
            tx.Type = TxType.Blob;
        }
        else
        {
            throw new Exception("Unsupported tx type");
        }
        return tx;

    }


    //Setup up the initial state
    private static void InitializeFromAlloc(Dictionary<Address, JsonTypes.AccountState> alloc, WorldState stateProvider, ISpecProvider specProvider)
    {
        foreach (KeyValuePair<Address, JsonTypes.AccountState> accountState in alloc)
        {
            foreach (KeyValuePair<string, string> storageItem in accountState.Value.Storage)
            {
                UInt256 storageKey = Bytes.FromHexString(storageItem.Key).ToUInt256();
                byte[] storageValue = Bytes.FromHexString(storageItem.Key);
                stateProvider.Set(new StorageCell(accountState.Key, storageKey), storageValue.WithoutLeadingZeros().ToArray());

            }
            stateProvider.CreateAccount(accountState.Key, Bytes.FromHexString(accountState.Value.Balance).ToUInt256(), Bytes.FromHexString(accountState.Value.Nonce).ToUInt256());
            stateProvider.InsertCode(accountState.Key, Bytes.FromHexString(accountState.Value.Code), specProvider.GenesisSpec);
        }
        stateProvider.Commit(specProvider.GenesisSpec);
        stateProvider.CommitTree(0);
        stateProvider.Reset();
    }
}
