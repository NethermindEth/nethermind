// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.LightClient.Consensus;
using NullLogManager = Nethermind.Logging.NullLogManager;
using Nethermind.Specs;

namespace Nethermind.LightClient.Verifier;

/// <summary>Runs the shared light-client consensus and execution-proof verifier without a network host.</summary>
/// <remarks>One owner serializes updates. Supplied journal records are replayed from the caller's trusted checkpoint.</remarks>
public sealed class PortableLightClient : IDisposable
{
    public const int MaxJournalBytes = 4 * 1024 * 1024;
    public const int MaxRecordBytes = 256 * 1024;
    private readonly BeaconChainSpec _spec;
    private readonly Hash256 _checkpoint;
    private readonly string _chainPath;
    private readonly string _headPath;
    private readonly string _optimisticPath;
    private readonly string _bestPath;
    private readonly VerifiedConsensusJournal _journal;
    private LightClientStore? _store;
    private bool _journalFailed;
    private readonly ISpecProvider _executionSpecs;
    private ProofRelayExecution? _relay;
    private VerifiedCall? _calls;
    private readonly Dictionary<Hash256, BlockHeader> _executionHeaders = [];

    public PortableLightClient(string network, string trustedCheckpoint, string journalDirectory)
    {
        _spec = network switch
        {
            "mainnet" => BeaconChainSpec.Mainnet,
            "hoodi" => BeaconChainSpec.Hoodi,
            "sepolia" => BeaconChainSpec.Sepolia,
            _ => throw new ArgumentException("Supported networks: mainnet, hoodi, sepolia.", nameof(network)),
        };
        _checkpoint = ParseHash(trustedCheckpoint);
        _executionSpecs = network switch
        {
            "mainnet" => MainnetSpecProvider.Instance,
            "hoodi" => HoodiSpecProvider.Instance,
            "sepolia" => SepoliaSpecProvider.Instance,
            _ => throw new ArgumentException("Unsupported execution network.", nameof(network)),
        };
        string prefix = Path.Combine(journalDirectory, $"{network}-{Convert.ToHexString(_checkpoint.Bytes)}");
        _chainPath = prefix + ".chain";
        _headPath = prefix + ".head";
        _optimisticPath = prefix + ".optimistic";
        _bestPath = prefix + ".best";
        _journal = new(journalDirectory, _spec, network, _checkpoint, NullLogger.Instance);
    }

    /// <summary>Revalidates a saved journal, or verifies a new bootstrap against the trusted checkpoint.</summary>
    public async Task InitializeAsync(byte[] bootstrap, byte[]? chain, byte[]? head, byte[]? optimistic, byte[]? best, CancellationToken token = default)
    {
        if (_store is not null) throw new InvalidOperationException("The portable verifier is already initialized.");
        Directory.CreateDirectory(Path.GetDirectoryName(_chainPath)!);
        if (chain is { Length: > 0 })
        {
            ValidateJournalSize(chain, head, optimistic, best);
            await File.WriteAllBytesAsync(_chainPath, chain, token);
            if (head is { Length: > 0 }) await File.WriteAllBytesAsync(_headPath, head, token);
            else if (File.Exists(_headPath)) File.Delete(_headPath);
            if (optimistic is { Length: > 0 }) await File.WriteAllBytesAsync(_optimisticPath, optimistic, token);
            else if (File.Exists(_optimisticPath)) File.Delete(_optimisticPath);
            if (best is { Length: > 0 }) await File.WriteAllBytesAsync(_bestPath, best, token);
            else if (File.Exists(_bestPath)) File.Delete(_bestPath);
            _store = await _journal.LoadAsync(CurrentSlot(), token)
                ?? throw new InvalidDataException("Saved consensus journal has no bootstrap.");
            return;
        }
        if (File.Exists(_chainPath)) File.Delete(_chainPath);
        if (File.Exists(_headPath)) File.Delete(_headPath);
        if (File.Exists(_optimisticPath)) File.Delete(_optimisticPath);
        if (File.Exists(_bestPath)) File.Delete(_bestPath);
        ValidateRecord(bootstrap);
        LightClientBootstrap decoded = LightClientWireCodec.DecodeBootstrap(bootstrap, _spec);
        LightClientStore store = new(_spec, _checkpoint, decoded, CurrentSlot());
        await _journal.InitializeAsync(decoded, store, token);
        _store = store;
    }

    /// <summary>Verifies canonical SSZ data and persists any resulting authenticated progress.</summary>
    /// <returns>Whether the record was applicable to the local clock and authenticated committee.</returns>
    public async Task<bool> ApplyAsync(string kind, byte[] record, CancellationToken token = default)
    {
        ValidateRecord(record);
        LightClientStore store = Store;
        ulong slot = CurrentSlot();
        try
        {
            switch (kind)
            {
                case "update":
                    LightClientUpdate update = LightClientWireCodec.DecodeUpdate(record, _spec);
                    store.Process(update, slot);
                    await PersistAsync(() => _journal.AppendAsync(update, store, token));
                    break;
                case "finality":
                    LightClientFinalityUpdate finality = LightClientWireCodec.DecodeFinality(record, _spec);
                    store.Process(finality, slot);
                    await PersistAsync(() => _journal.AppendAsync(finality, store, token));
                    break;
                case "optimistic":
                    LightClientOptimisticUpdate optimistic = LightClientWireCodec.DecodeOptimistic(record, _spec);
                    store.Process(optimistic, slot);
                    await PersistAsync(() => _journal.AppendAsync(optimistic, store, token));
                    break;
                default:
                    throw new ArgumentException("Unknown consensus record kind.", nameof(kind));
            }
        }
        catch (IOException exception) when (exception is IrrelevantLightClientUpdateException or LightClientLocalStateException)
        {
            return false;
        }
        TrimExecutionHeaders();
        return true;
    }

    /// <summary>Exports the authenticated journal for atomic device storage.</summary>
    public async Task<(byte[] Chain, byte[]? Head, byte[]? Optimistic, byte[]? Best)> ExportJournalAsync(CancellationToken token = default)
    {
        _ = Store;
        byte[] chain = await File.ReadAllBytesAsync(_chainPath, token);
        byte[]? head = File.Exists(_headPath) ? await File.ReadAllBytesAsync(_headPath, token) : null;
        byte[]? optimistic = File.Exists(_optimisticPath) ? await File.ReadAllBytesAsync(_optimisticPath, token) : null;
        byte[]? best = File.Exists(_bestPath) ? await File.ReadAllBytesAsync(_bestPath, token) : null;
        ValidateJournalSize(chain, head, optimistic, best);
        return (chain, head, optimistic, best);
    }

    /// <summary>Uses a previously verified update for shared consensus timeout recovery.</summary>
    public async Task<bool> RecoverAfterTimeoutAsync(CancellationToken token = default)
    {
        LightClientStore store = Store;
        LightClientUpdate? best = store.BestUpdate;
        ulong slot = CurrentSlot();
        if (best is null || !store.ForceUpdate(slot)) return false;
        await PersistAsync(() => _journal.AppendForcedAsync(best, slot, store, token));
        TrimExecutionHeaders();
        return true;
    }

    /// <summary>Accepts an execution header only when its canonical RLP hashes to a consensus-authenticated execution hash.</summary>
    public void AuthenticateExecutionHeader(string selector, byte[] encoded)
    {
        LightClientHeader selected = SelectHeader(selector);
        Hash256 expected = ExecutionHash(selected);
        BlockHeader header = ProofRelayExecution.DecodeHeader(encoded, expected, _executionSpecs);
        if (selected.Execution is { } payload && (header.Number != payload.BlockNumber || header.StateRoot != payload.StateRoot))
            throw new InvalidDataException("Execution header differs from the authenticated payload.");
        _executionHeaders[expected] = header;
        TrimExecutionHeaders();
    }

    /// <summary>Returns a detached JSON description of locally authenticated consensus and execution state.</summary>
    public string GetHead(string selector)
    {
        LightClientHeader header = SelectHeader(selector);
        Hash256 hash = ExecutionHash(header);
        BlockHeader? execution = _executionHeaders.GetValueOrDefault(hash);
        return WriteJson(writer =>
        {
            writer.WriteString("chainId", $"0x{_spec.ChainId:x}");
            writer.WriteString("checkpoint", _checkpoint.ToString());
            writer.WriteNumber("period", Store.Period);
            writer.WriteBoolean("nextCommitteeKnown", Store.NextSyncCommitteeKnown);
            writer.WriteNumber("slot", header.Beacon!.Slot);
            writer.WriteString("blockHash", hash.ToString());
            if (header.Execution is { } payload)
            {
                writer.WriteString("blockNumber", $"0x{payload.BlockNumber:x}");
                writer.WriteString("stateRoot", payload.StateRoot!.ToString());
            }
            else if (execution is not null)
            {
                writer.WriteString("blockNumber", $"0x{execution.Number:x}");
                writer.WriteString("stateRoot", execution.StateRoot!.ToString());
            }
        });
    }

    /// <summary>Reports synchronization state even while the authenticated execution head is stale.</summary>
    public string GetSyncStatus() => WriteJson(writer =>
    {
        writer.WriteString("chainId", $"0x{_spec.ChainId:x}");
        writer.WriteNumber("period", Store.Period);
        writer.WriteNumber("currentPeriod", _spec.GetEpoch(CurrentSlot()) / Presets.EpochsPerSyncCommitteePeriod);
        writer.WriteBoolean("nextCommitteeKnown", Store.NextSyncCommitteeKnown);
        writer.WriteNumber("finalizedSlot", Store.FinalizedHeader.Beacon!.Slot);
        writer.WriteNumber("optimisticSlot", Store.OptimisticHeader.Beacon!.Slot);
    });

    /// <summary>Verifies an account path and returns values decoded from its authenticated RLP.</summary>
    public string VerifyAccount(string selector, string address, string proofJson)
    {
        Account account = ExecutionProofVerifier.VerifyAccount(StateRoot(selector), ParseAddress(address), ParseProof(proofJson));
        return WriteJson(writer =>
        {
            writer.WriteString("balance", account.Balance.ToHexString(skipLeadingZeros: true));
            writer.WriteString("nonce", account.Nonce.ToHexString(skipLeadingZeros: true));
            writer.WriteString("codeHash", account.CodeHash.ToString());
            writer.WriteString("storageHash", account.StorageRoot.ToString());
        });
    }

    /// <summary>Verifies a storage path after authenticating the owning account against the selected state.</summary>
    public string VerifyStorage(string selector, string address, string accountProofJson, string key, string storageProofJson)
    {
        Account account = ExecutionProofVerifier.VerifyAccount(StateRoot(selector), ParseAddress(address), ParseProof(accountProofJson));
        byte[] keyBytes = ParseHex(key, 32);
        UInt256 slot = new(keyBytes, isBigEndian: true);
        UInt256 value = ExecutionProofVerifier.VerifyStorage(account.StorageRoot, slot, ParseProof(storageProofJson));
        return value.ToBigEndian().ToHexString(withZeroX: true);
    }

    /// <summary>Verifies bytecode against a proof-authenticated account, then returns the code bytes.</summary>
    public string VerifyCode(string selector, string address, string accountProofJson, string code)
    {
        Account account = ExecutionProofVerifier.VerifyAccount(StateRoot(selector), ParseAddress(address), ParseProof(accountProofJson));
        byte[] bytes = ParseHex(code, 1024 * 1024);
        ExecutionProofVerifier.VerifyCode(account.CodeHash, bytes);
        return bytes.ToHexString(withZeroX: true);
    }

    /// <summary>Configures proof-serving HTTP transports for locally executed EVM requests.</summary>
    public void ConfigureExecution(string[] relays)
    {
        if (_relay is not null) throw new InvalidOperationException("Execution transports are already configured.");
        if (relays.Length is < 1 or > 4) throw new ArgumentException("One to four proof relays are required.", nameof(relays));
        foreach (string relay in relays)
        {
            Uri uri = new(relay, UriKind.Absolute);
            if (uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
                throw new ArgumentException("Proof relays require HTTPS or loopback HTTP.", nameof(relays));
        }
        _relay = new([.. relays], _executionSpecs);
        _calls = new(_relay, _executionSpecs, NullLogManager.Instance);
    }

    /// <summary>Runs the shared Nethermind EVM locally over fetched and verified state.</summary>
    public async Task<string> ExecuteAsync(string selector, string transactionJson, bool estimateGas, CancellationToken token = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        token = deadline.Token;
        VerifiedCall calls = _calls ?? throw new InvalidOperationException("Proof transports are not configured.");
        if (transactionJson.Length > 128 * 1024) throw new ArgumentException("Transaction object exceeds its size limit.", nameof(transactionJson));
        LightClientHeader selected = SelectHeader(selector);
        Hash256 hash = ExecutionHash(selected);
        BlockHeader executionHeader = await _relay!.GetHeaderByHashAsync(hash, token);
        Hash256 stateRoot = selected.Execution?.StateRoot ?? executionHeader.StateRoot!;
        ulong number = selected.Execution?.BlockNumber ?? executionHeader.Number;
        VerifiedHead head = new(selected.Beacon!.Slot, number, hash, stateRoot);
        using JsonDocument request = JsonDocument.Parse(transactionJson);
        return await calls.ExecuteAsync(head, request.RootElement, estimateGas, token);
    }

    public void Dispose()
    {
        _calls?.Dispose();
        _relay?.Dispose();
    }

    private LightClientStore Store => _journalFailed ? throw new InvalidOperationException("Consensus journal failed; reload and reverify the saved records.")
        : _store ?? throw new InvalidOperationException("The portable verifier is not initialized.");
    private ulong CurrentSlot() => _spec.GetSlotAtTime((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private LightClientHeader SelectHeader(string selector)
    {
        LightClientHeader header = selector switch
        {
            "finalized" => Store.FinalizedHeader,
            "latest" => Store.OptimisticHeader,
            _ => throw new ArgumentException("Browser state selectors are finalized or latest.", nameof(selector)),
        };
        ulong slot = header.Beacon!.Slot;
        ulong now = CurrentSlot();
        if (slot > now || now - slot > (selector == "latest" ? 5UL * 60 : 60UL * 60) / _spec.SecondsPerSlot)
            throw new InvalidDataException("Authenticated light-client head is stale or in the future.");
        return header;
    }

    private Hash256 StateRoot(string selector)
    {
        LightClientHeader header = SelectHeader(selector);
        return header.Execution?.StateRoot ?? _executionHeaders.GetValueOrDefault(ExecutionHash(header))?.StateRoot
            ?? throw new InvalidOperationException("The authenticated execution header is required for this fork.");
    }

    private static Hash256 ExecutionHash(LightClientHeader header) => header.Execution?.BlockHash ?? header.ExecutionBlockHash
        ?? throw new InvalidDataException("Authenticated beacon header has no execution hash.");

    private void TrimExecutionHeaders()
    {
        Hash256 finalized = ExecutionHash(Store.FinalizedHeader);
        Hash256 latest = ExecutionHash(Store.OptimisticHeader);
        foreach (Hash256 hash in _executionHeaders.Keys.ToArray())
            if (hash != finalized && hash != latest) _executionHeaders.Remove(hash);
    }

    private static byte[][] ParseProof(string json)
    {
        if (json.Length > 2 * ExecutionProofVerifier.MaxProofNodes * (ExecutionProofVerifier.MaxNodeBytes + 8))
            throw new InvalidDataException("Execution proof exceeds its size limit.");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement list = document.RootElement;
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > ExecutionProofVerifier.MaxProofNodes)
            throw new InvalidDataException("Execution proof has an invalid node count.");
        return [.. list.EnumerateArray().Select(value => ParseHex(value.GetString()!, ExecutionProofVerifier.MaxNodeBytes))];
    }

    private static Address ParseAddress(string value)
    {
        byte[] bytes = ParseHex(value, 20);
        if (bytes.Length != 20) throw new ArgumentException("Address must contain 20 bytes.", nameof(value));
        return new(bytes);
    }

    private static Hash256 ParseHash(string value)
    {
        byte[] bytes = ParseHex(value, 32);
        if (bytes.Length != 32) throw new ArgumentException("Hash must contain 32 bytes.", nameof(value));
        return new(bytes);
    }

    private static byte[] ParseHex(string value, int maxBytes)
    {
        if (!value.StartsWith("0x", StringComparison.Ordinal) || (value.Length & 1) != 0 || value.Length > 2 + 2 * maxBytes)
            throw new ArgumentException("Invalid hexadecimal data.", nameof(value));
        return Convert.FromHexString(value.AsSpan(2));
    }

    private static void ValidateRecord(byte[] record)
    {
        if (record.Length is 0 or > MaxRecordBytes) throw new InvalidDataException("Consensus record exceeds its size limit.");
    }

    private async Task PersistAsync(Func<Task> write)
    {
        try
        {
            await write();
            if (new FileInfo(_chainPath).Length > MaxJournalBytes)
                throw new InvalidDataException("Browser consensus journal reached its size limit; supply a newer checkpoint.");
        }
        catch
        {
            _journalFailed = true;
            throw;
        }
    }

    private static void ValidateJournalSize(byte[] chain, byte[]? head, byte[]? optimistic, byte[]? best)
    {
        if (chain.Length > MaxJournalBytes || head?.Length > MaxRecordBytes + 108 || optimistic?.Length > MaxRecordBytes + 108
            || best?.Length > MaxRecordBytes + 172)
            throw new InvalidDataException("Browser consensus journal exceeds its size limit; supply a newer checkpoint.");
    }

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
