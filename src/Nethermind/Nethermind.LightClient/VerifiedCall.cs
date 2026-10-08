// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;

namespace Nethermind.LightClient;

/// <summary>Executes calls against authenticated finalized state, fetching missing state between clean EVM attempts.</summary>
internal sealed class VerifiedCall(IExecutionStateSource execution, ISpecProvider specs, ILogManager logs) : IDisposable
{
    private const ulong GasCap = 10_000_000;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
    private readonly SemaphoreSlim _slots = new(4);
    private readonly EthereumJsonSerializer _json = new();

    internal async Task<string> ExecuteAsync(VerifiedHead head, System.Text.Json.JsonElement request, bool estimateGas, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        CancellationToken token = deadline.Token;
        await _slots.WaitAsync(token);
        try { return await ExecuteCoreAsync(head, request, estimateGas, token); }
        finally { _slots.Release(); }
    }

    private async Task<string> ExecuteCoreAsync(VerifiedHead head, System.Text.Json.JsonElement request, bool estimateGas, CancellationToken token)
    {
        TransactionForRpc model;
        try { model = _json.Deserialize<TransactionForRpc>(request.GetRawText()) ?? throw new FormatException(); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or FormatException or ArgumentException)
        {
            throw new RpcException(-32602, "Invalid transaction object.");
        }
        if (estimateGas && model.Gas == 0) model.Gas = null;
        BlockHeader header = await GetHeaderAsync(head, token);
        IReleaseSpec spec = specs.GetSpec(header);
        Nethermind.Core.Result<Transaction> converted = model.ToValidatedTransaction(gasCap: GasCap, spec: spec);
        if (converted.IsError) throw new RpcException(-32602, converted.Error);
        Transaction tx = converted.Data;
        tx.SenderAddress ??= Address.Zero;
        if (tx.Type == TxType.Blob || tx.Type == TxType.FrameTx)
            throw new RpcException(-32602, "Blob and frame transactions are not supported by verified execution.");
        tx.Hash = tx.Type <= TxType.FrameTx ? null : tx.CalculateHash();

        for (int attempt = 0; attempt < 128; attempt++)
        {
            token.ThrowIfCancellationRequested();
            CallState backend = new(head, _cache);
            WorldState state = new(backend, logs);
            if (!state.TryBeginScope(header, out IDisposable? scope)) throw new RpcException(-32000, "Verified state is unavailable.");
            using (scope)
            {
                try
                {
                    tx.Nonce = state.GetNonce(tx.SenderAddress);
                    BlockHeader callHeader = header.Clone();
                    callHeader.IsPostMerge = true;
                    callHeader.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(tx);
                    IBlockhashProvider blockhashes = new VerifiedBlockhashes(state, head, _cache);
                    EthereumVirtualMachine vm = new(blockhashes, specs, logs);
                    CodeInfoRepository code = new(state, new EthereumPrecompileProvider());
                    EthereumTransactionProcessor processor = new(BlobBaseFeeCalculator.Instance, specs, state, vm, code, logs);
                    BlockExecutionContext context = new(callHeader, spec);
                    if (estimateGas)
                    {
                        GasEstimation estimation = new GasEstimator(processor, state).Estimate(tx, in context, gasCap: GasCap, token: token);
                        if (estimation.Reverted)
                            throw new RpcException(3, estimation.Error ?? "execution reverted", (estimation.RevertData ?? []).ToHexString(withZeroX: true));
                        if (estimation.Error is not null)
                            throw new RpcException(-32000, estimation.Error);
                        return $"0x{estimation.Gas:x}";
                    }
                    CallOutputTracer tracer = new();
                    TransactionResult result = processor.CallAndRestore(tx, in context, tracer.WithCancellation(token));
                    string? error = result.GetErrorMessage(tracer.Error);
                    if (result.EvmExceptionType == EvmExceptionType.Revert)
                        throw new RpcException(3, error ?? "execution reverted", (tracer.ReturnValue ?? []).ToHexString(withZeroX: true));
                    if (error is not null)
                        throw new RpcException(-32000, error);
                    return (tracer.ReturnValue ?? []).ToHexString(withZeroX: true);
                }
                catch (MissingCallState missing)
                {
                    await FillAsync(head, missing, token);
                }
            }
        }
        throw new RpcException(-32000, "Execution accessed too many distinct state items.");
    }

    private async Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken token)
    {
        object key = (head.BlockHash, "header");
        if (_cache.TryGetValue(key, out BlockHeader? cached)) return cached!;
        BlockHeader header = await execution.GetHeaderAsync(head, token);
        if (header.Hash != head.BlockHash || header.Number != head.Number || header.StateRoot != head.StateRoot)
            throw new RpcException(-32000, "Execution header does not match verified finality.");
        _cache.Set(key, header, new MemoryCacheEntryOptions().SetSize(1024));
        return header;
    }

    private async Task FillAsync(VerifiedHead head, MissingCallState missing, CancellationToken token)
    {
        switch (missing.Kind)
        {
            case MissingKind.Account:
                Account account = await execution.GetAccountAsync(head, missing.Address!, token);
                _cache.Set((head.StateRoot, missing.Address!), account, new MemoryCacheEntryOptions().SetSize(256));
                break;
            case MissingKind.Storage:
                if (!_cache.TryGetValue((head.StateRoot, missing.Address!), out Account? owner) || owner is null)
                    throw new RpcException(-32000, "Account proof is unavailable for storage.");
                UInt256 slot = await execution.GetStorageAsync(head, missing.Address!, owner, missing.Slot, token);
                _cache.Set((head.StateRoot, missing.Address!, missing.Slot), slot, new MemoryCacheEntryOptions().SetSize(64));
                break;
            case MissingKind.Code:
                Account? codeOwner = null;
                foreach (Address address in missing.KnownAccounts!)
                {
                    if (_cache.TryGetValue((head.StateRoot, address), out Account? candidate)
                        && candidate is not null && candidate.CodeHash.ValueHash256 == missing.CodeHash)
                    {
                        codeOwner = candidate;
                        break;
                    }
                }
                if (codeOwner is null) throw new RpcException(-32000, "Code hash has no authenticated account.");
                byte[] code = await execution.GetCodeAsync(codeOwner, token);
                _cache.Set((head.StateRoot, missing.CodeHash), code, new MemoryCacheEntryOptions().SetSize(Math.Max(1, code.Length)));
                break;
            case MissingKind.Blockhash:
                Hash256[] hashes = await execution.GetAncestorHashesAsync(head, missing.BlockNumber, token);
                _cache.Set((head.BlockHash, "ancestors"), (missing.BlockNumber, hashes), new MemoryCacheEntryOptions().SetSize(hashes.Length * 32L));
                break;
        }
    }

    public void Dispose()
    {
        _slots.Dispose();
        _cache.Dispose();
    }

    private enum MissingKind { Account, Storage, Code, Blockhash }

    private sealed class MissingCallState(MissingKind kind) : Exception
    {
        public MissingKind Kind { get; } = kind;
        public Address? Address { get; init; }
        public UInt256 Slot { get; init; }
        public ValueHash256 CodeHash { get; init; }
        public IReadOnlyList<Address>? KnownAccounts { get; init; }
        public ulong BlockNumber { get; init; }
    }

    private sealed class CallState(VerifiedHead head, MemoryCache cache) : IWorldStateScopeProvider
    {
        public bool HasRoot(BlockHeader? baseBlock) => baseBlock?.StateRoot == head.StateRoot;
        public bool HasStateForTargetBlock(BlockHeader targetBlock) => false;
        public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            scope = null;
            return false;
        }
        public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            scope = HasRoot(baseBlock) ? new Scope(head, cache) : null;
            return scope is not null;
        }

        private sealed class Scope(VerifiedHead head, MemoryCache cache) : IWorldStateScopeProvider.IScope, IWorldStateScopeProvider.ICodeDb
        {
            private readonly Dictionary<ValueHash256, byte[]> _newCode = [];
            private readonly HashSet<Address> _accounts = [];
            public Hash256 RootHash => head.StateRoot;
            public IWorldStateScopeProvider.ICodeDb CodeDb => this;
            public Account? Get(Address address)
            {
                _accounts.Add(address);
                if (!cache.TryGetValue((head.StateRoot, address), out Account? account))
                    throw new MissingCallState(MissingKind.Account) { Address = address };
                return account == Account.TotallyEmpty ? null : account;
            }
            public void HintGet(Address address, Account? account) { }
            public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
            {
                Account? account = Get(address);
                return new Storage(head, address, account?.StorageRoot ?? Keccak.EmptyTreeHash, cache);
            }
            public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash)
            {
                if (_newCode.TryGetValue(codeHash, out byte[]? staged)) return staged;
                if (cache.TryGetValue((head.StateRoot, codeHash), out byte[]? code)) return code!;
                throw new MissingCallState(MissingKind.Code) { CodeHash = codeHash, KnownAccounts = [.. _accounts] };
            }
            public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new CodeSetter(_newCode);
            public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => new WriteBatch();
            public void UpdateRootHash() { }
            public void Commit(ulong blockNumber) { }
            public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => Task.CompletedTask;
            public void ApplyBal(ReadOnlyBlockAccessList bal) => throw new NotSupportedException();
            public void Dispose() { }
        }

        private sealed class Storage(VerifiedHead head, Address address, Hash256 root, MemoryCache cache) : IWorldStateScopeProvider.IStorageTree
        {
            public Hash256 RootHash => root;
            public void Get(in UInt256 index, out UInt256 value)
            {
                if (root == Keccak.EmptyTreeHash) { value = UInt256.Zero; return; }
                if (!cache.TryGetValue((head.StateRoot, address, index), out value))
                    throw new MissingCallState(MissingKind.Storage) { Address = address, Slot = index };
            }
            public void HintSet(in UInt256 index) { }
        }

        private sealed class CodeSetter(Dictionary<ValueHash256, byte[]> code) : IWorldStateScopeProvider.ICodeSetter
        {
            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> bytes) => code[codeHash] = bytes.ToArray();
            public void Dispose() { }
        }

        private sealed class WriteBatch : IWorldStateScopeProvider.IWorldStateWriteBatch
        {
            public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;
            public void Set(Address key, Account? account) => OnAccountUpdated?.Invoke(this, new(key, account));
            public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) => new StorageWriteBatch();
            public void Dispose() { }
        }

        private sealed class StorageWriteBatch : IWorldStateScopeProvider.IStorageWriteBatch
        {
            public void Set(in UInt256 index, in UInt256 value) { }
            public void Clear() { }
            public void Dispose() { }
        }
    }

    private sealed class VerifiedBlockhashes(IWorldState state, VerifiedHead head, MemoryCache cache) : IBlockhashProvider
    {
        private readonly BlockhashStore _store = new(state);
        public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec)
        {
            if (number >= currentBlock.Number) return null;
            if (spec.IsBlockHashInStateAvailable) return _store.GetBlockHashFromState(currentBlock, number, spec);
            ulong depth = currentBlock.Number - number;
            if (depth > 256) return null;
            if (depth == 1) return currentBlock.ParentHash;
            if (cache.TryGetValue((head.BlockHash, "ancestors"), out (ulong First, Hash256[] Hashes) ancestors)
                && number >= ancestors.First && number - ancestors.First < (ulong)ancestors.Hashes.Length)
                return ancestors.Hashes[number - ancestors.First];
            throw new MissingCallState(MissingKind.Blockhash) { BlockNumber = number };
        }
        public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
    }
}
