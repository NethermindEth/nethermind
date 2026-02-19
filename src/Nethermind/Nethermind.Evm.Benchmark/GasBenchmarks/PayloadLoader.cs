// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Db;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Loads engine_newPayloadV4 payload files and genesis state for gas benchmarks.
/// </summary>
public static class PayloadLoader
{
    private static readonly object s_genesisLock = new();
    private static IDbProvider s_dbProvider;
    private static TrieStore s_trieStore;
    private static IDb s_codeDb;
    private static Hash256 s_genesisStateRoot;
    private static bool s_genesisInitialized;

    /// <summary>
    /// Parses an engine_newPayloadV4 payload file and returns a BlockHeader + decoded transactions.
    /// </summary>
    public static (BlockHeader Header, Transaction[] Transactions) LoadPayload(string filePath)
    {
        string firstLine;
        using (StreamReader reader = new(filePath))
        {
            firstLine = reader.ReadLine();
        }

        using JsonDocument doc = JsonDocument.Parse(firstLine);
        JsonElement paramsArray = doc.RootElement.GetProperty("params");
        JsonElement payload = paramsArray[0];

        long blockNumber = ParseHexLong(payload, "blockNumber");
        long gasLimit = ParseHexLong(payload, "gasLimit");
        long gasUsed = ParseHexLong(payload, "gasUsed");
        ulong timestamp = ParseHexULong(payload, "timestamp");
        UInt256 baseFeePerGas = ParseHexUInt256(payload, "baseFeePerGas");
        Hash256 parentHash = new(Bytes.FromHexString(payload.GetProperty("parentHash").GetString()));
        Hash256 prevRandao = new(Bytes.FromHexString(payload.GetProperty("prevRandao").GetString()));
        Address beneficiary = new(payload.GetProperty("feeRecipient").GetString());

        BlockHeader header = new(
            parentHash,
            Keccak.OfAnEmptySequenceRlp,
            beneficiary,
            UInt256.Zero,
            blockNumber,
            gasLimit,
            timestamp,
            Array.Empty<byte>())
        {
            GasUsed = gasUsed,
            BaseFeePerGas = baseFeePerGas,
            MixHash = prevRandao,
            IsPostMerge = true,
            Hash = new Hash256(Bytes.FromHexString(payload.GetProperty("blockHash").GetString()))
        };

        JsonElement txsArray = payload.GetProperty("transactions");
        int txCount = txsArray.GetArrayLength();
        Transaction[] transactions = new Transaction[txCount];

        EthereumEcdsa ecdsa = new(1);
        for (int i = 0; i < txCount; i++)
        {
            byte[] rlpBytes = Bytes.FromHexString(txsArray[i].GetString());
            transactions[i] = TxDecoder.Instance.Decode(rlpBytes);
            transactions[i].SenderAddress = ecdsa.RecoverAddress(transactions[i]);
        }

        return (header, transactions);
    }

    /// <summary>
    /// Parses an engine_newPayloadV4 payload file and returns a full Block suitable for BlockProcessor.
    /// Includes withdrawals, TxRoot, WithdrawalsRoot, and other block-level fields.
    /// </summary>
    public static Block LoadBlock(string filePath)
    {
        string firstLine;
        using (StreamReader reader = new(filePath))
        {
            firstLine = reader.ReadLine();
        }

        return ParseBlockFromJsonRpcLine(firstLine);
    }

    /// <summary>
    /// Loads all engine_newPayloadV4 blocks from a setup file.
    /// Setup files may contain multiple JSON-RPC calls (newPayload + forkchoiceUpdated pairs).
    /// Only engine_newPayloadV4 lines are parsed; other lines (forkchoiceUpdated) are skipped.
    /// </summary>
    public static Block[] LoadAllSetupBlocks(string filePath)
    {
        string[] allLines = File.ReadAllLines(filePath);
        List<Block> blocks = new();

        for (int i = 0; i < allLines.Length; i++)
        {
            string line = allLines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Only parse engine_newPayloadV4 calls, skip forkchoiceUpdated and others
            if (!line.Contains("\"engine_newPayloadV4\""))
                continue;

            blocks.Add(ParseBlockFromJsonRpcLine(line));
        }

        return blocks.ToArray();
    }

    private static Block ParseBlockFromJsonRpcLine(string jsonLine)
    {
        using JsonDocument doc = JsonDocument.Parse(jsonLine);
        JsonElement paramsArray = doc.RootElement.GetProperty("params");
        JsonElement payload = paramsArray[0];

        long blockNumber = ParseHexLong(payload, "blockNumber");
        long gasLimit = ParseHexLong(payload, "gasLimit");
        long gasUsed = ParseHexLong(payload, "gasUsed");
        ulong timestamp = ParseHexULong(payload, "timestamp");
        UInt256 baseFeePerGas = ParseHexUInt256(payload, "baseFeePerGas");
        Hash256 parentHash = new(Bytes.FromHexString(payload.GetProperty("parentHash").GetString()));
        Hash256 prevRandao = new(Bytes.FromHexString(payload.GetProperty("prevRandao").GetString()));
        Address beneficiary = new(payload.GetProperty("feeRecipient").GetString());

        // Parse blob gas fields (EIP-4844)
        ulong? blobGasUsed = payload.TryGetProperty("blobGasUsed", out JsonElement blobGasEl) ? ParseHexULong(blobGasEl.GetString()) : null;
        ulong? excessBlobGas = payload.TryGetProperty("excessBlobGas", out JsonElement excessEl) ? ParseHexULong(excessEl.GetString()) : null;

        // Parse parent beacon block root (EIP-4788) — separate JSON-RPC param, not inside payload object
        Hash256 parentBeaconBlockRoot = null;
        if (paramsArray.GetArrayLength() > 2 && paramsArray[2].ValueKind == JsonValueKind.String)
        {
            string beaconRootHex = paramsArray[2].GetString();
            if (beaconRootHex is not null && beaconRootHex.Length > 2)
                parentBeaconBlockRoot = new Hash256(Bytes.FromHexString(beaconRootHex));
        }

        // Decode transactions
        JsonElement txsArray = payload.GetProperty("transactions");
        int txCount = txsArray.GetArrayLength();
        Transaction[] transactions = new Transaction[txCount];

        EthereumEcdsa ecdsa = new(1);
        for (int i = 0; i < txCount; i++)
        {
            byte[] rlpBytes = Bytes.FromHexString(txsArray[i].GetString());
            transactions[i] = TxDecoder.Instance.Decode(rlpBytes);
            transactions[i].SenderAddress = ecdsa.RecoverAddress(transactions[i]);
        }

        // Parse withdrawals (EIP-4895)
        Withdrawal[] withdrawals = null;
        if (payload.TryGetProperty("withdrawals", out JsonElement withdrawalsEl) && withdrawalsEl.ValueKind == JsonValueKind.Array)
        {
            int wCount = withdrawalsEl.GetArrayLength();
            withdrawals = new Withdrawal[wCount];
            for (int i = 0; i < wCount; i++)
            {
                JsonElement w = withdrawalsEl[i];
                withdrawals[i] = new Withdrawal
                {
                    Index = ParseHexULong(w, "index"),
                    ValidatorIndex = ParseHexULong(w, "validatorIndex"),
                    Address = new Address(w.GetProperty("address").GetString()),
                    AmountInGwei = ParseHexULong(w, "amount"),
                };
            }
        }

        // Build header with full block-level fields
        BlockHeader header = new(
            parentHash,
            Keccak.OfAnEmptySequenceRlp,
            beneficiary,
            UInt256.Zero,
            blockNumber,
            gasLimit,
            timestamp,
            Array.Empty<byte>(),
            blobGasUsed,
            excessBlobGas)
        {
            GasUsed = gasUsed,
            BaseFeePerGas = baseFeePerGas,
            MixHash = prevRandao,
            IsPostMerge = true,
            Hash = new Hash256(Bytes.FromHexString(payload.GetProperty("blockHash").GetString())),
            ParentBeaconBlockRoot = parentBeaconBlockRoot,
            TxRoot = TxTrie.CalculateRoot(transactions),
            WithdrawalsRoot = withdrawals is not null ? WithdrawalTrie.CalculateRoot(withdrawals) : null,
        };

        return new Block(header, new BlockBody(transactions, Array.Empty<BlockHeader>(), withdrawals));
    }

    /// <summary>
    /// Ensures the genesis state is loaded from the chainspec file into a shared TrieStore.
    /// Subsequent calls are no-ops. Call CreateWorldState() to get a WorldState rooted at genesis.
    /// </summary>
    public static void EnsureGenesisInitialized(string genesisPath, IReleaseSpec spec)
    {
        if (s_genesisInitialized) return;

        lock (s_genesisLock)
        {
            if (s_genesisInitialized) return;

            KzgPolynomialCommitments.InitializeAsync().GetAwaiter().GetResult();
            s_dbProvider = TestMemDbProvider.Init();
            PruningConfig pruningConfig = new();
            TestFinalizedStateProvider finalizedStateProvider = new(pruningConfig.PruningBoundary);

            s_trieStore = new TrieStore(
                new NodeStorage(s_dbProvider.StateDb),
                No.Pruning,
                Persist.EveryBlock,
                finalizedStateProvider,
                pruningConfig,
                LimboLogs.Instance);

            finalizedStateProvider.TrieStore = s_trieStore;
            s_codeDb = s_dbProvider.CodeDb;

            WorldState state = new(
                new TrieStoreScopeProvider(s_trieStore, s_codeDb, LimboLogs.Instance),
                LimboLogs.Instance);

            using (state.BeginScope(IWorldState.PreGenesis))
            {
                LoadGenesisAccounts(state, genesisPath, spec);
                state.Commit(spec);
                state.CommitTree(0);
                s_genesisStateRoot = state.StateRoot;
            }

            s_genesisInitialized = true;
        }
    }

    public static Hash256 GenesisStateRoot
    {
        get
        {
            if (!s_genesisInitialized)
                throw new InvalidOperationException("Genesis not initialized.");
            return s_genesisStateRoot;
        }
    }

    /// <summary>
    /// Creates a new WorldState backed by the shared TrieStore (which contains the genesis trie).
    /// Caller must call BeginScope with a BlockHeader whose StateRoot is GenesisStateRoot.
    /// </summary>
    public static IWorldState CreateWorldState(
        NodeStorageCache nodeStorageCache = null,
        PreBlockCaches preBlockCaches = null,
        bool populatePreBlockCache = true)
    {
        if (!s_genesisInitialized)
            throw new InvalidOperationException("Genesis not initialized. Call EnsureGenesisInitialized first.");

        ITrieStore trieStore = s_trieStore;
        if (nodeStorageCache is not null)
        {
            trieStore = new PreCachedTrieStore(trieStore, nodeStorageCache);
        }

        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(trieStore, s_codeDb, LimboLogs.Instance);
        if (preBlockCaches is not null)
        {
            scopeProvider = new PrewarmerScopeProvider(scopeProvider, preBlockCaches, populatePreBlockCache);
        }

        return new WorldState(scopeProvider, LimboLogs.Instance);
    }

    public static IWorldStateManager CreateWorldStateManager(NodeStorageCache nodeStorageCache = null)
    {
        if (!s_genesisInitialized)
            throw new InvalidOperationException("Genesis not initialized. Call EnsureGenesisInitialized first.");

        ITrieStore trieStore = s_trieStore;
        if (nodeStorageCache is not null)
        {
            trieStore = new PreCachedTrieStore(trieStore, nodeStorageCache);
        }

        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(trieStore, s_codeDb, LimboLogs.Instance);
        return new WorldStateManager(scopeProvider, s_trieStore, s_dbProvider, LimboLogs.Instance);
    }

    private static void LoadGenesisAccounts(IWorldState state, string genesisPath, IReleaseSpec spec)
    {
        if (!File.Exists(genesisPath))
        {
            string message = $"Genesis file not found: {genesisPath}\n" +
                "Make sure the gas-benchmarks submodule is initialized:\n" +
                "  git lfs install && git submodule update --init tools/gas-benchmarks";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                message += "\n  On Windows, you may also need: git config --global core.longpaths true";
            throw new FileNotFoundException(message);
        }

        using FileStream fs = File.OpenRead(genesisPath);

        // Detect Git LFS pointer (starts with "version https://git-lfs")
        byte[] header = new byte[8];
        int read = fs.Read(header, 0, header.Length);
        fs.Position = 0;

        if (read >= 7 && header[0] == (byte)'v' && header[1] == (byte)'e' && header[2] == (byte)'r')
        {
            throw new InvalidOperationException(
                $"Genesis file appears to be a Git LFS pointer: {genesisPath}\n" +
                "Git LFS was not installed when the submodule was cloned. Fix with:\n" +
                "  git lfs install && cd tools/gas-benchmarks && git lfs pull");
        }

        using JsonDocument doc = JsonDocument.Parse(fs);
        JsonElement accounts = doc.RootElement.GetProperty("accounts");

        foreach (JsonProperty entry in accounts.EnumerateObject())
        {
            // Skip builtin precompile definitions
            if (entry.Value.TryGetProperty("builtin", out _))
                continue;

            Address address = new(entry.Name);

            UInt256 balance = UInt256.Zero;
            if (entry.Value.TryGetProperty("balance", out JsonElement balanceEl))
                UInt256.TryParse(balanceEl.GetString(), out balance);

            state.CreateAccount(address, balance);

            if (entry.Value.TryGetProperty("nonce", out JsonElement nonceEl))
            {
                UInt256 nonce = ParseHexUInt256(nonceEl.GetString());
                if (nonce > UInt256.Zero)
                    state.IncrementNonce(address, nonce);
            }

            if (entry.Value.TryGetProperty("code", out JsonElement codeEl))
            {
                string codeHex = codeEl.GetString();
                if (codeHex is not null && codeHex.Length > 2)
                {
                    byte[] code = Bytes.FromHexString(codeHex);
                    ValueHash256 codeHash = ValueKeccak.Compute(code);
                    state.InsertCode(address, in codeHash, code, spec, isGenesis: true);
                }
            }

            if (entry.Value.TryGetProperty("storage", out JsonElement storageEl))
            {
                foreach (JsonProperty storageEntry in storageEl.EnumerateObject())
                {
                    UInt256 slot = ParseHexUInt256(storageEntry.Name);
                    byte[] value = Bytes.FromHexString(storageEntry.Value.GetString());
                    state.Set(new StorageCell(address, slot), value);
                }
            }
        }
    }

    /// <summary>
    /// Reads the raw JSON-RPC line from a payload file. Used by NewPayload mode to measure deserialization.
    /// </summary>
    public static string ReadRawJson(string filePath)
    {
        using StreamReader reader = new(filePath);
        return reader.ReadLine();
    }

    /// <summary>
    /// Parses expected stateRoot and blockHash from a payload file for verification.
    /// </summary>
    public static (Hash256 StateRoot, Hash256 BlockHash) ParseExpectedHashes(string filePath)
    {
        string firstLine = ReadRawJson(filePath);

        using JsonDocument doc = JsonDocument.Parse(firstLine);
        JsonElement payload = doc.RootElement.GetProperty("params")[0];

        Hash256 stateRoot = null;
        if (payload.TryGetProperty("stateRoot", out JsonElement stateRootEl))
        {
            string hex = stateRootEl.GetString();
            if (hex is not null && hex.Length > 2)
                stateRoot = new Hash256(Bytes.FromHexString(hex));
        }

        Hash256 blockHash = null;
        if (payload.TryGetProperty("blockHash", out JsonElement blockHashEl))
        {
            string hex = blockHashEl.GetString();
            if (hex is not null && hex.Length > 2)
                blockHash = new Hash256(Bytes.FromHexString(hex));
        }

        return (stateRoot, blockHash);
    }

    /// <summary>
    /// Verifies a processed block's state root and block hash against expected values from the payload.
    /// </summary>
    public static void VerifyProcessedBlock(Block processedBlock, string scenarioName, string filePath)
    {
        (Hash256 expectedStateRoot, Hash256 expectedBlockHash) = ParseExpectedHashes(filePath);

        if (expectedStateRoot is not null && processedBlock.Header.StateRoot != expectedStateRoot)
        {
            throw new InvalidOperationException(
                $"State root mismatch for {scenarioName}!\n" +
                $"  Expected: {expectedStateRoot}\n" +
                $"  Computed: {processedBlock.Header.StateRoot}\n" +
                "Block processing produced incorrect results.");
        }

        if (expectedBlockHash is not null && processedBlock.Header.Hash != expectedBlockHash)
        {
            throw new InvalidOperationException(
                $"Block hash mismatch for {scenarioName}!\n" +
                $"  Expected: {expectedBlockHash}\n" +
                $"  Computed: {processedBlock.Header.Hash}\n" +
                $"  StateRoot match: {processedBlock.Header.StateRoot == expectedStateRoot}\n" +
                "Block processing produced a different block hash — some header field differs.");
        }
    }

    private static long ParseHexLong(JsonElement parent, string propertyName)
    {
        string hex = parent.GetProperty(propertyName).GetString();
        return Convert.ToInt64(hex, 16);
    }

    private static ulong ParseHexULong(JsonElement parent, string propertyName)
    {
        string hex = parent.GetProperty(propertyName).GetString();
        return ParseHexULong(hex);
    }

    private static ulong ParseHexULong(string hex)
    {
        return Convert.ToUInt64(hex, 16);
    }

    private static UInt256 ParseHexUInt256(JsonElement parent, string propertyName)
    {
        string hex = parent.GetProperty(propertyName).GetString();
        return ParseHexUInt256(hex);
    }

    private static UInt256 ParseHexUInt256(string hex)
    {
        if (hex is null || hex == "0x" || hex == "0x0" || hex == "0x00")
            return UInt256.Zero;

        ReadOnlySpan<char> hexSpan = hex.AsSpan(2);
        UInt256.TryParse(hexSpan, System.Globalization.NumberStyles.HexNumber, null, out UInt256 result);
        return result;
    }
}
