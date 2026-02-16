// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
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
    /// Ensures the genesis state is loaded from the chainspec file into a shared TrieStore.
    /// Subsequent calls are no-ops. Call CreateWorldState() to get a WorldState rooted at genesis.
    /// </summary>
    public static void EnsureGenesisInitialized(string genesisPath, IReleaseSpec spec)
    {
        if (s_genesisInitialized) return;

        lock (s_genesisLock)
        {
            if (s_genesisInitialized) return;

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
    public static IWorldState CreateWorldState()
    {
        if (!s_genesisInitialized)
            throw new InvalidOperationException("Genesis not initialized. Call EnsureGenesisInitialized first.");

        return new WorldState(
            new TrieStoreScopeProvider(s_trieStore, s_codeDb, LimboLogs.Instance),
            LimboLogs.Instance);
    }

    private static void LoadGenesisAccounts(IWorldState state, string genesisPath, IReleaseSpec spec)
    {
        if (!File.Exists(genesisPath))
        {
            throw new FileNotFoundException(
                $"Genesis file not found: {genesisPath}\n" +
                "Make sure the gas-benchmarks submodule is initialized:\n" +
                "  git lfs install && git submodule update --init tools/gas-benchmarks");
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

    private static long ParseHexLong(JsonElement parent, string propertyName)
    {
        string hex = parent.GetProperty(propertyName).GetString();
        return Convert.ToInt64(hex, 16);
    }

    private static ulong ParseHexULong(JsonElement parent, string propertyName)
    {
        string hex = parent.GetProperty(propertyName).GetString();
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
