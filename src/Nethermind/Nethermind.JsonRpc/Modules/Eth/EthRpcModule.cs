// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Db.LogIndex;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Contract.P2P;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Trie;
using Nethermind.TxPool;
using Nethermind.Wallet;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Crypto;
using Block = Nethermind.Core.Block;
using BlockHeader = Nethermind.Core.BlockHeader;
using ResultType = Nethermind.Core.ResultType;
using Signature = Nethermind.Core.Crypto.Signature;
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.JsonRpc.Modules.Eth;

public partial class EthRpcModule(
    IJsonRpcConfig rpcConfig,
    IBlockchainBridge blockchainBridge,
    IBlockFinder blockFinder,
    IBlockTree blockTree,
    IReceiptFinder receiptFinder,
    IStateReader stateReader,
    ITxPool txPool,
    ITxSender txSender,
    IWallet wallet,
    ILogManager logManager,
    ISpecProvider specProvider,
    IGasPriceOracle gasPriceOracle,
    IEthSyncingInfo ethSyncingInfo,
    IFeeHistoryOracle feeHistoryOracle,
    IProtocolsManager protocolsManager,
    IForkInfo forkInfo,
    ILogIndexConfig? logIndexConfig,
    IReceiptConfig receiptConfig,
    ulong? secondsPerSlot,
    HeadBlockSignal headBlockSignal,
    IEthCapabilitiesProvider capabilitiesProvider,
    IBlockForRpcFactory blockForRpcFactory) : IEthRpcModule
{
    public const int GetProofStorageKeyLimit = 1000;
    public const int MaxGetStorageSlots = StorageValuesRequest.MaxSlots;
    protected readonly Encoding _messageEncoding = Encoding.UTF8;
    protected readonly IJsonRpcConfig _rpcConfig = rpcConfig ?? throw new ArgumentNullException(nameof(rpcConfig));
    protected readonly IBlockchainBridge _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
    protected readonly IBlockFinder _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
    private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
    protected readonly IReceiptFinder _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
    protected readonly IStateReader _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
    protected readonly ITxPool _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
    protected readonly ITxSender _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
    protected readonly IWallet _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
    protected readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
    protected readonly IBlockForRpcFactory _blockForRpcFactory = blockForRpcFactory ?? throw new ArgumentNullException(nameof(blockForRpcFactory));
    protected readonly ILogger _logger = logManager.GetClassLogger<EthRpcModule>();
    private static readonly TxDecoder TxRlpDecoder = TxDecoder.Instance;
    protected readonly IGasPriceOracle _gasPriceOracle = gasPriceOracle ?? throw new ArgumentNullException(nameof(gasPriceOracle));
    private readonly IEthCapabilitiesProvider _capabilitiesProvider = capabilitiesProvider ?? throw new ArgumentNullException(nameof(capabilitiesProvider));
    protected readonly IEthSyncingInfo _ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
    protected readonly IFeeHistoryOracle _feeHistoryOracle = feeHistoryOracle ?? throw new ArgumentNullException(nameof(feeHistoryOracle));
    protected readonly IProtocolsManager _protocolsManager = protocolsManager ?? throw new ArgumentNullException(nameof(protocolsManager));
    protected readonly ulong _secondsPerSlot = secondsPerSlot ?? throw new ArgumentNullException(nameof(secondsPerSlot));
    private readonly HeadBlockSignal _headBlockSignal = headBlockSignal ?? throw new ArgumentNullException(nameof(headBlockSignal));
    private readonly IReceiptConfig _receiptConfig = receiptConfig ?? throw new ArgumentNullException(nameof(receiptConfig));
    private ResultWrapper<ulong>? _chainIdResponse;
    readonly JsonSerializerOptions UnchangedDictionaryKeyOptions = new(EthereumJsonSerializer.JsonOptionsIndented) { DictionaryKeyPolicy = null };

    public ResultWrapper<string> eth_protocolVersion()
    {
        int highestVersion = _protocolsManager.GetHighestProtocolVersion(Protocol.Eth);
        return ResultWrapper<string>.Success(highestVersion.ToHexString());
    }

    public ResultWrapper<SyncingResult> eth_syncing() => ResultWrapper<SyncingResult>.Success(_ethSyncingInfo.GetFullInfo());

    public ResultWrapper<byte[]> eth_snapshot() => ResultWrapper<byte[]>.Fail("eth_snapshot not supported");

    public ResultWrapper<Address> eth_coinbase() => ResultWrapper<Address>.Success(Address.Zero);

    public async Task<ResultWrapper<UInt256?>> eth_gasPrice() => ResultWrapper<UInt256?>.Success(await _gasPriceOracle.GetGasPriceEstimate());

    public ResultWrapper<UInt256?> eth_blobBaseFee()
    {
        BlockHeader? head = _blockFinder.Head?.Header;
        if (head?.ExcessBlobGas is null)
        {
            return ResultWrapper<UInt256?>.Success(null);
        }

        IReleaseSpec spec = _specProvider.GetSpec(head);
        if (!BlobGasCalculator.TryCalculateFeePerBlobGas(head.ExcessBlobGas.Value,
                spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas))
        {
            return ResultWrapper<UInt256?>.Fail("Unable to calculate the current blob base fee");
        }
        return ResultWrapper<UInt256?>.Success(feePerBlobGas);
    }

    public ResultWrapper<UInt256?> eth_baseFee()
    {
        BlockHeader? head = _blockFinder.Head?.Header;
        if (head is null)
        {
            return ResultWrapper<UInt256?>.Success(null);
        }

        IEip1559Spec specFor1559 = _specProvider.GetSpecFor1559(head.Number + 1);
        if (!specFor1559.IsEip1559Enabled)
        {
            return ResultWrapper<UInt256?>.Success(null);
        }

        return ResultWrapper<UInt256?>.Success(BaseFeeCalculator.Calculate(head, specFor1559));
    }

    public ResultWrapper<UInt256?> eth_maxPriorityFeePerGas()
    {
        UInt256 gasPriceWithBaseFee = _gasPriceOracle.GetMaxPriorityGasFeeEstimate();
        return ResultWrapper<UInt256?>.Success(gasPriceWithBaseFee);
    }

    public ResultWrapper<FeeHistoryResults> eth_feeHistory(ulong blockCount, BlockParameter newestBlock, double[] rewardPercentiles) => _feeHistoryOracle.GetFeeHistory(blockCount, newestBlock, rewardPercentiles);

    public ResultWrapper<IEnumerable<Address>> eth_accounts()
    {
        try
        {
            return ResultWrapper<IEnumerable<Address>>.Success(_wallet.GetAccounts());
        }
        catch (Exception)
        {
            return ResultWrapper<IEnumerable<Address>>.Fail("Error while getting key addresses from wallet.");
        }
    }

    public Task<ResultWrapper<ulong?>> eth_blockNumber()
    {
        ulong number = _blockchainBridge.HeadBlock?.Number ?? 0;
        return Task.FromResult(ResultWrapper<ulong?>.Success(number));
    }

    public Task<ResultWrapper<UInt256?>> eth_getBalance(Address address, BlockParameter? blockParameter = null)
    {
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return Task.FromResult(GetFailureResult<UInt256?, BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet()));
        }

        BlockHeader header = searchResult.Object;
        if (!_blockchainBridge.HasStateForBlock(header!))
        {
            return Task.FromResult(GetStateFailureResult<UInt256?>(header));
        }

        _stateReader.TryGetAccount(header!, address, out AccountStruct account);
        return Task.FromResult(ResultWrapper<UInt256?>.Success(account.Balance));
    }

    public ResultWrapper<byte[]> eth_getStorageAt(Address address, StorageIndex positionIndex,
        BlockParameter? blockParameter = null)
    {
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return GetFailureResult<byte[], BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet());
        }

        BlockHeader? header = searchResult.Object;
        if (!_blockchainBridge.HasStateForBlock(header!))
        {
            return GetStateFailureResult<byte[]>(header!);
        }

        try
        {
            ReadOnlySpan<byte> storage = _stateReader.GetStorage(header!, address, positionIndex);
            return ResultWrapper<byte[]>.Success(storage.IsEmpty ? Bytes32.Zero.Unwrap() : storage!.PadLeft(32));
        }
        catch (MissingTrieNodeException e)
        {
            Hash256 hash = e.Hash;
            return ResultWrapper<byte[]>.Fail($"missing trie node {hash} (path ) state {hash} is not available", ErrorCodes.ResourceNotFound);
        }
    }

    public ResultWrapper<StorageValuesResult> eth_getStorageValues(
        StorageValuesRequest requests,
        BlockParameter? blockParameter = null)
    {
        if (requests.TooManySlots)
            return TooManySlotsError();

        if (requests.Entries.Count == 0 || requests.TotalSlots == 0)
            return ResultWrapper<StorageValuesResult>.Fail("empty request", ErrorCodes.InvalidParams);

        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
            return GetFailureResult<StorageValuesResult, BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet());

        BlockHeader header = searchResult.Object!;
        if (!_blockchainBridge.HasStateForBlock(header))
            return GetStateFailureResult<StorageValuesResult>(header);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(requests.TotalSlots * 32);
        buffer.AsSpan(0, requests.TotalSlots * 32).Clear();
        int bufferOffset = 0;

        Dictionary<Address, Memory<byte>[]> slots = new(requests.Entries.Count);
        foreach (KeyValuePair<Address, UInt256[]> entry in requests.Entries)
        {
            UInt256[] slotKeys = entry.Value;
            Memory<byte>[] values = new Memory<byte>[slotKeys.Length];
            for (int i = 0; i < slotKeys.Length; i++)
            {
                ReadOnlySpan<byte> storage = _stateReader.GetStorage(header, entry.Key, slotKeys[i]);
                Memory<byte> slot = buffer.AsMemory(bufferOffset, 32);
                if (!storage.IsEmpty)
                    storage.CopyTo(slot.Span[(32 - storage.Length)..]);
                values[i] = slot;
                bufferOffset += 32;
            }
            slots[entry.Key] = values;
        }

        return ResultWrapper<StorageValuesResult>.Success(new StorageValuesResult(buffer, slots));
    }

    private static ResultWrapper<StorageValuesResult> TooManySlotsError() =>
        ResultWrapper<StorageValuesResult>.Fail($"too many slots (max {MaxGetStorageSlots})", ErrorCodes.InvalidParams);

    public virtual Task<ResultWrapper<UInt256>> eth_getTransactionCount(Address address, BlockParameter? blockParameter)
    {
        if (blockParameter == BlockParameter.Pending)
        {
            UInt256 pendingNonce = _txPool.GetLatestPendingNonce(address);
            return Task.FromResult(ResultWrapper<UInt256>.Success(pendingNonce));
        }

        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return Task.FromResult(GetFailureResult<UInt256, BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet()));
        }

        BlockHeader header = searchResult.Object;
        if (!_blockchainBridge.HasStateForBlock(header!))
        {
            return Task.FromResult(GetStateFailureResult<UInt256>(header));
        }

        _stateReader.TryGetAccount(header!, address, out AccountStruct account);
        return Task.FromResult(ResultWrapper<UInt256>.Success(account.Nonce));
    }

    public ResultWrapper<UInt256?> eth_getBlockTransactionCountByHash(Hash256 blockHash)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(new BlockParameter(blockHash));
        return searchResult.IsError
            ? ResultWrapper<UInt256?>.Success(null)
            : ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object!.Transactions.Length);
    }

    public ResultWrapper<UInt256?> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        return searchResult.IsError
            ? ResultWrapper<UInt256?>.Success(null)
            : ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object!.Transactions.Length);
    }

    public ResultWrapper<UInt256?> eth_getUncleCountByBlockHash(Hash256 blockHash)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(new BlockParameter(blockHash));
        return searchResult.IsError
            ? ResultWrapper<UInt256?>.Success(null)
            : ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object!.Uncles.Length);
    }

    public ResultWrapper<UInt256?> eth_getUncleCountByBlockNumber(BlockParameter? blockParameter)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        return searchResult.IsError
            ? ResultWrapper<UInt256?>.Success(null)
            : ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object!.Uncles.Length);
    }

    public ResultWrapper<byte[]> eth_getCode(Address address, BlockParameter? blockParameter = null)
    {
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return GetFailureResult<byte[], BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet());
        }

        BlockHeader header = searchResult.Object;
        return !_blockchainBridge.HasStateForBlock(header!)
            ? GetStateFailureResult<byte[]>(header)
            : ResultWrapper<byte[]>.Success(
                _stateReader.TryGetAccount(header!, address, out AccountStruct account)
                    ? _stateReader.GetCode(account.CodeHash)
                    : []);
    }

    public ResultWrapper<Signature> eth_sign(Address addressData, byte[] message)
    {
        if (!_wallet.TrySignMessage(message, addressData, out Signature sig))
            return ResultWrapper<Signature>.Fail("authentication needed: password or unlock", ErrorCodes.AccountLocked);

        if (_logger.IsTrace) _logger.Trace($"eth_sign request {addressData}, {message}, result: {sig}");
        return ResultWrapper<Signature>.Success(sig);
    }

    public virtual Task<ResultWrapper<Hash256>> eth_sendTransaction(SignableTransactionForRpc rpcTx)
    {
        Result<Transaction> txResult = rpcTx.ToTransaction(validateUserInput: true);
        if (!txResult.Success(out Transaction tx, out string error))
        {
            return Task.FromResult(ResultWrapper<Hash256>.Fail(error, ErrorCodes.InvalidInput));
        }

        tx.ChainId = _blockchainBridge.GetChainId();

        UInt256? nonce = rpcTx is LegacyTransactionForRpc legacy ? legacy.Nonce : null;

        TxHandlingOptions options = nonce is null ? TxHandlingOptions.ManagedNonce : TxHandlingOptions.None;
        return SendTx(tx, options);
    }

    public virtual async Task<ResultWrapper<Hash256>> eth_sendRawTransaction(byte[] transaction)
    {
        try
        {
            Transaction tx = TxRlpDecoder.DecodeCompleteNotNull(transaction,
                RlpBehaviors.AllowUnsigned | RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);
            return await SendTx(tx);
        }
        catch (RlpException)
        {
            return ResultWrapper<Hash256>.Fail("Invalid RLP.", ErrorCodes.TransactionRejected);
        }
    }

    public virtual ResultWrapper<SignTransactionResult> eth_signTransaction(SignableTransactionForRpc rpcTx)
    {
        if (!_rpcConfig.EnableEthSignTransaction)
            return ResultWrapper<SignTransactionResult>.Fail("eth_signTransaction is disabled", ErrorCodes.MethodNotFound);

        Address from = (rpcTx as LegacyTransactionForRpc)?.From ?? Address.Zero;
        if (!_wallet.IsUnlocked(from))
            return ResultWrapper<SignTransactionResult>.Fail("authentication needed: password or unlock", ErrorCodes.InvalidInput);

        Result<Transaction> txResult = rpcTx.ToSignableTransaction();
        if (!txResult.Success(out Transaction tx, out string error))
            return ResultWrapper<SignTransactionResult>.Fail(error, ErrorCodes.InvalidInput);

        ulong chainId = _blockchainBridge.GetChainId();
        tx.ChainId = chainId;

        ResultWrapper<SignTransactionResult>? feeCapError = CheckTxFeeCap(tx);
        if (feeCapError is not null)
            return feeCapError;

        // Sidecar must be attached before encode; signing only sets tx.Signature so the wrapper survives.
        if (rpcTx is BlobTransactionForRpc blobTx)
        {
            ProofVersion version = _blockFinder.Head?.Header is { } head
                ? _specProvider.GetSpec(head).BlobProofVersion
                : ProofVersion.V0;
            if (blobTx.TryAttachSidecar(tx, version) is { } attachError)
                return ResultWrapper<SignTransactionResult>.Fail(attachError, ErrorCodes.InvalidInput);
        }

        if (!_wallet.TrySignTransaction(tx, chainId))
            return ResultWrapper<SignTransactionResult>.Fail("authentication needed: password or unlock", ErrorCodes.InvalidInput);

        tx.Hash = tx.CalculateHash();

        if (_logger.IsInfo) _logger.Info($"eth_signTransaction signed tx {tx.Hash} from {tx.SenderAddress}");

        return BuildSignedResult(tx);
    }

    private static ResultWrapper<SignTransactionResult> BuildSignedResult(Transaction tx)
    {
        const RlpBehaviors encodeBehaviors = RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm;
        ArrayPoolList<byte> buffer = TxDecoder.Instance.EncodeToArrayPoolList(tx, encodeBehaviors);
        try
        {
            return ResultWrapper<SignTransactionResult>.Success(new SignTransactionResult
            {
                Raw = buffer,
                Tx = TransactionForRpc.FromTransaction(tx)
            });
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private ResultWrapper<SignTransactionResult>? CheckTxFeeCap(Transaction tx)
    {
        ulong cap = _rpcConfig.RpcTxFeeCap;
        if (cap == 0) return null;

        // Cap covers execution gas only (maxFeePerGas * gasLimit). Blob gas (4844) is intentionally
        // excluded — keeps parity with the reference implementation.
        UInt256 perGas = tx.Type >= TxType.EIP1559 ? tx.MaxFeePerGas : tx.GasPrice;
        UInt256 capWei = cap;

        // Reject overflow as cap-exceeded: a wraparound multiplication would otherwise let huge
        // fee values silently slip through.
        bool overflow = UInt256.MultiplyOverflow(perGas, (UInt256)tx.GasLimit, out UInt256 totalFee);
        if (!overflow && totalFee <= capWei) return null;

        decimal capEth = capWei.ToDecimal(null) / (decimal)Unit.Ether;
        string message = overflow
            ? string.Create(CultureInfo.InvariantCulture, $"tx fee (overflow ether) exceeds the configured cap ({capEth:F2} ether)")
            : string.Create(CultureInfo.InvariantCulture, $"tx fee ({totalFee.ToDecimal(null) / (decimal)Unit.Ether:F2} ether) exceeds the configured cap ({capEth:F2} ether)");
        return ResultWrapper<SignTransactionResult>.Fail(message, ErrorCodes.InvalidInput);
    }

    public virtual async Task<ResultWrapper<FillTransactionResult>> eth_fillTransaction(SignableTransactionForRpc rpcTx)
    {
        BlockHeader? head = _blockFinder.Head?.Header;
        if (head is null)
            return ResultWrapper<FillTransactionResult>.Fail("No head block available", ErrorCodes.ResourceUnavailable);

        IReleaseSpec spec = _specProvider.GetSpec(head);
        ulong chainId = _blockchainBridge.GetChainId();

        LegacyTransactionForRpc legacyTx = (LegacyTransactionForRpc)rpcTx;
        if (legacyTx.From is not { } from)
            return ResultWrapper<FillTransactionResult>.Fail("from address not specified", ErrorCodes.InvalidInput);

        if (legacyTx.ChainId is { } requestedChainId && requestedChainId != chainId)
            return ResultWrapper<FillTransactionResult>.Fail($"invalid chain id (have={chainId}, want={requestedChainId})", ErrorCodes.InvalidInput);

        legacyTx.Nonce ??= _txPool.GetLatestPendingNonce(from);

        UInt256? blobBaseFee = head.ExcessBlobGas is { } excessBlobGas
            && BlobGasCalculator.TryCalculateFeePerBlobGas(excessBlobGas, spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas)
            ? feePerBlobGas
            : null;

        TxFillContext fillContext = new()
        {
            GasPrice = await _gasPriceOracle.GetGasPriceEstimate(),
            MaxPriorityFeePerGas = _gasPriceOracle.GetMaxPriorityGasFeeEstimate(),
            BaseFee = BaseFeeCalculator.Calculate(head, _specProvider.GetSpecFor1559(head.Number + 1)),
            BlobBaseFee = blobBaseFee,
            Spec = spec,
        };

        Result fillResult = rpcTx.FillDefaults(fillContext);
        if (!fillResult)
            return ResultWrapper<FillTransactionResult>.Fail(fillResult.Error!, ErrorCodes.InvalidInput);

        if (rpcTx.Gas is null)
        {
            ResultWrapper<UInt256?> gasEstimate = eth_estimateGas(rpcTx, BlockParameter.Latest);
            if (gasEstimate.Result.ResultType != ResultType.Success)
                return ResultWrapper<FillTransactionResult>.Fail(gasEstimate.Result.Error ?? "gas estimation failed", gasEstimate.ErrorCode);

            rpcTx.Gas = (ulong)gasEstimate.Data!.Value;
        }

        legacyTx.ChainId ??= chainId;

        Result<Transaction> txResult = rpcTx.ToTransaction(validateUserInput: true, gasCap: _rpcConfig.GasCap, spec: spec);
        if (!txResult.Success(out Transaction tx, out string error))
            return ResultWrapper<FillTransactionResult>.Fail(error, ErrorCodes.InvalidInput);

        tx.ChainId = chainId;

        if (rpcTx is BlobTransactionForRpc { Blobs: not null } withSidecar
            && withSidecar.TryAttachSidecar(tx, spec.BlobProofVersion) is { } attachError)
        {
            return ResultWrapper<FillTransactionResult>.Fail(attachError, ErrorCodes.InvalidInput);
        }

        return ResultWrapper<FillTransactionResult>.Success(new FillTransactionResult
        {
            Tx = TransactionForRpc.FromTransaction(tx)
        });
    }

    public async Task<ResultWrapper<ReceiptForRpc?>> eth_sendRawTransactionSync(byte[] transaction, ulong? timeoutMs = null)
    {
        int waitMs = ResolveSyncTimeoutMs(timeoutMs);
        using CancellationTokenSource cts = new(waitMs);

        // Submit via the virtual eth_sendRawTransaction so subclass overrides
        // propagate without needing a separate sync override.
        ResultWrapper<Hash256> sendResult = await eth_sendRawTransaction(transaction);
        if (sendResult.Result.ResultType != ResultType.Success)
        {
            return ResultWrapper<ReceiptForRpc?>.Fail(sendResult.Result.Error ?? "Send failed", sendResult.ErrorCode);
        }
        Hash256 hash = sendResult.Data;

        while (true)
        {
            // Snapshot the next-head Task BEFORE the receipt check: if a head arrives between
            // the check and the await, the snapshot is already completed and the loop re-checks
            // immediately. Snapshotting after the check would miss that signal.
            Task nextHead = _headBlockSignal.NextHeadTask;

            ResultWrapper<ReceiptForRpc?> receiptResult = eth_getTransactionReceipt(hash);
            if (receiptResult.Data is not null)
            {
                return receiptResult;
            }

            try
            {
                await nextHead.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return ResultWrapper<ReceiptForRpc?>.Fail(
                    $"Transaction {hash} was added to the pool but not included within {waitMs}ms.",
                    ErrorCodes.Timeout);
            }
        }
    }

    private int ResolveSyncTimeoutMs(ulong? requestedMs)
    {
        // Clamp >0 so a negative max can't wrap to a huge ulong and overflow back to a negative delay.
        int max = Math.Max(1, _rpcConfig.RpcTxSyncMaxTimeoutMs);
        if (requestedMs is { } req && req > 0)
        {
            return (int)Math.Min(req, (ulong)max);
        }
        int @default = _rpcConfig.RpcTxSyncDefaultTimeoutMs;
        return @default > 0 ? Math.Min(@default, max) : max;
    }

    private async Task<ResultWrapper<Hash256>> SendTx(Transaction tx,
        TxHandlingOptions txHandlingOptions = TxHandlingOptions.None)
    {
        try
        {
            (Hash256 txHash, AcceptTxResult? acceptTxResult) =
                await _txSender.SendTransaction(tx, txHandlingOptions | TxHandlingOptions.PersistentBroadcast);

            if (acceptTxResult.Equals(AcceptTxResult.Accepted))
                return ResultWrapper<Hash256>.Success(txHash);

            return acceptTxResult.Equals(AcceptTxResult.SignFailed)
                ? ResultWrapper<Hash256>.Fail("authentication needed: password or unlock", ErrorCodes.AccountLocked)
                : ResultWrapper<Hash256>.Fail(acceptTxResult?.ToString() ?? string.Empty, ErrorCodes.TransactionRejected);
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Failed to send transaction.", e);
            return ResultWrapper<Hash256>.Fail(e.Message, ErrorCodes.TransactionRejected);
        }
    }

    public virtual ResultWrapper<HexBytes> eth_call(SignableTransactionForRpc transactionCall, BlockParameter? blockParameter = null, Dictionary<Address, AccountOverride>? stateOverride = null, BlockOverride? blockOverride = null) =>
        new CallTxExecutor(_blockchainBridge, _blockFinder, _rpcConfig, _specProvider)
            .ExecuteTx(transactionCall, blockParameter, stateOverride, blockOverride);

    public ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> eth_simulateV1(SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null) =>
        new SimulateTxExecutor<SimulateCallResult>(_blockchainBridge, _blockFinder, _rpcConfig, _specProvider, new SimulateBlockMutatorTracerFactory(), secondsPerSlot: _secondsPerSlot)
            .Execute(payload, blockParameter);

    public virtual ResultWrapper<UInt256?> eth_estimateGas(SignableTransactionForRpc transactionCall, BlockParameter? blockParameter, Dictionary<Address, AccountOverride>? stateOverride = null, BlockOverride? blockOverride = null) =>
        new EstimateGasTxExecutor(_blockchainBridge, _blockFinder, _rpcConfig, _specProvider)
            .ExecuteTx(transactionCall, blockParameter, stateOverride, blockOverride);

    public virtual ResultWrapper<AccessListResultForRpc?> eth_createAccessList(SignableTransactionForRpc transactionCall, BlockParameter? blockParameter = null, Dictionary<Address, AccountOverride>? stateOverride = null, bool optimize = true) =>
        new CreateAccessListTxExecutor(_blockchainBridge, _blockFinder, _rpcConfig, _specProvider, optimize)
            .ExecuteTx(transactionCall, blockParameter, stateOverride);

    public ResultWrapper<BlockForRpc> eth_getBlockByHash(Hash256 blockHash, bool returnFullTransactionObjects) => GetBlock(new BlockParameter(blockHash), returnFullTransactionObjects);

    public ResultWrapper<BlockForRpc> eth_getBlockByNumber(BlockParameter blockParameter,
        bool returnFullTransactionObjects) => GetBlock(blockParameter, returnFullTransactionObjects);

    protected virtual ResultWrapper<BlockForRpc?> GetBlock(BlockParameter blockParameter, bool returnFullTransactionObjects)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter, true);
        if (searchResult.IsError)
        {
            return ResultWrapper<BlockForRpc?>.Success(null);
        }

        Block block = searchResult.Object!;
        if (returnFullTransactionObjects && block is not null)
        {
            _blockchainBridge.RecoverTxSenders(block);
        }

        if (block is null)
        {
            return ResultWrapper<BlockForRpc?>.Success(null);
        }

        BlockForRpc? blockForRpc = _blockForRpcFactory.Create(block, returnFullTransactionObjects, _specProvider);
        if (blockForRpc is null)
        {
            return ResultWrapper<BlockForRpc?>.Success(null);
        }

        if (blockParameter.Type == BlockParameterType.Pending)
        {
            blockForRpc.Hash = null;
            blockForRpc.Nonce = null;
            blockForRpc.Miner = null;
        }

        return ResultWrapper<BlockForRpc?>.Success(blockForRpc);
    }

    public ResultWrapper<BlockHeaderForRpc?> eth_getHeaderByHash(Hash256 blockHash)
        => GetHeader(new BlockParameter(blockHash));

    public ResultWrapper<BlockHeaderForRpc?> eth_getHeaderByNumber(BlockParameter blockParameter)
        => GetHeader(blockParameter);

    private ResultWrapper<BlockHeaderForRpc?> GetHeader(BlockParameter blockParameter)
    {
        // SearchForHeader avoids loading the block body — header endpoints don't need transactions/uncles.
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return ResultWrapper<BlockHeaderForRpc?>.Success(null);
        }

        BlockHeaderForRpc result = _blockForRpcFactory.CreateHeader(searchResult.Object!, _specProvider);
        if (blockParameter.Type == BlockParameterType.Pending)
        {
            result.Hash = null;
            result.Nonce = null;
            result.Miner = null;
        }

        return ResultWrapper<BlockHeaderForRpc?>.Success(result);
    }

    public virtual ResultWrapper<TransactionForRpc?> eth_getTransactionByHash(Hash256 transactionHash)
    {
        if (!_blockchainBridge.TryGetTransaction(transactionHash, out TransactionLookupResult? transactionResult, checkTxnPool: true))
        {
            return ResultWrapper<TransactionForRpc?>.Success(null);
        }

        RecoverTxSenderIfNeeded(transactionResult.Value.Transaction);
        TransactionForRpcContext extraData = transactionResult.Value.ExtraData;
        TransactionForRpc transactionModel = TransactionForRpc.FromTransaction(
            transaction: transactionResult.Value.Transaction,
            extraData: extraData);
        if (_logger.IsTrace) _logger.Trace($"eth_getTransactionByHash request {transactionHash}, result: {transactionModel.Hash}");
        return ResultWrapper<TransactionForRpc?>.Success(transactionModel);
    }

    public ResultWrapper<ArrayPoolList<byte>?> eth_getRawTransactionByHash(Hash256 transactionHash)
    {
        if (!_blockchainBridge.TryGetTransaction(transactionHash, out TransactionLookupResult? transactionResult, checkTxnPool: true))
        {
            return ResultWrapper<ArrayPoolList<byte>?>.Success(null);
        }

        Transaction transaction = transactionResult.Value.Transaction;

        RlpBehaviors encodingSettings = RlpBehaviors.SkipTypedWrapping | (transaction.IsInMempoolForm() ? RlpBehaviors.InMempoolForm : RlpBehaviors.None);
        return ResultWrapper<ArrayPoolList<byte>?>.Success(TxRlpDecoder.EncodeToArrayPoolList(transaction, encodingSettings));
    }

    public virtual ResultWrapper<TransactionForRpc[]> eth_pendingTransactions()
    {
        Transaction[] transactions = _txPool.GetPendingTransactions();
        TransactionForRpc[] transactionsModels = new TransactionForRpc[transactions.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction transaction = transactions[i];
            RecoverTxSenderIfNeeded(transaction);
            transactionsModels[i] = TransactionForRpc.FromTransaction(transaction, new(_specProvider.ChainId));
            transactionsModels[i].BlockHash = Keccak.Zero;
        }

        if (_logger.IsTrace) _logger.Trace($"eth_pendingTransactions request, result: {transactionsModels.Length}");
        return ResultWrapper<TransactionForRpc[]>.Success(transactionsModels);
    }

    public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockHashAndIndex(Hash256 blockHash, UInt256 positionIndex)
    {
        ResultWrapper<TransactionForRpc> result = GetTransactionByBlockAndIndex(new BlockParameter(blockHash), positionIndex);
        if (_logger.IsTrace && result.Result.ResultType == ResultType.Success) _logger.Trace($"eth_getTransactionByBlockHashAndIndex request {blockHash}, index: {positionIndex}, result: {result.Data?.Hash}");
        return result;
    }

    public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
    {
        ResultWrapper<TransactionForRpc> result = GetTransactionByBlockAndIndex(blockParameter, positionIndex);
        if (_logger.IsTrace && result.Result.ResultType == ResultType.Success) _logger.Trace($"eth_getTransactionByBlockNumberAndIndex request {blockParameter}, index: {positionIndex}, result: {result.Data?.Hash}");
        return result;
    }

    public ResultWrapper<ArrayPoolList<byte>?> eth_getRawTransactionByBlockHashAndIndex(Hash256 blockHash, UInt256 positionIndex)
    {
        ResultWrapper<ArrayPoolList<byte>?> result = GetRawTransactionByBlockAndIndex(new BlockParameter(blockHash), positionIndex);
        if (_logger.IsTrace && result.Result.ResultType == ResultType.Success) _logger.Trace($"eth_getRawTransactionByBlockHashAndIndex request {blockHash}, index: {positionIndex}, result length: {result.Data?.Count ?? 0}");
        return result;
    }

    public ResultWrapper<ArrayPoolList<byte>?> eth_getRawTransactionByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
    {
        ResultWrapper<ArrayPoolList<byte>?> result = GetRawTransactionByBlockAndIndex(blockParameter, positionIndex);
        if (_logger.IsTrace && result.Result.ResultType == ResultType.Success) _logger.Trace($"eth_getRawTransactionByBlockNumberAndIndex request {blockParameter}, index: {positionIndex}, result length: {result.Data?.Count ?? 0}");
        return result;
    }

    private ResultWrapper<ArrayPoolList<byte>?> GetRawTransactionByBlockAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        if (searchResult.IsError)
        {
            return ResultWrapper<ArrayPoolList<byte>?>.Success(null);
        }

        Block block = searchResult.Object!;
        if (positionIndex >= block.Transactions.Length)
        {
            return ResultWrapper<ArrayPoolList<byte>?>.Success(null);
        }

        Transaction transaction = block.Transactions[(int)positionIndex];
        // Block-stored txs never carry a sidecar (blob commitments live separately), so consensus form only.
        return ResultWrapper<ArrayPoolList<byte>?>.Success(TxRlpDecoder.EncodeToArrayPoolList(transaction, RlpBehaviors.SkipTypedWrapping));
    }

    protected virtual ResultWrapper<TransactionForRpc?> GetTransactionByBlockAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        if (searchResult.IsError)
        {
            return ResultWrapper<TransactionForRpc?>.Success(null);
        }

        Block block = searchResult.Object!;
        if (positionIndex >= block.Transactions.Length)
        {
            return ResultWrapper<TransactionForRpc?>.Success(null);
        }

        Transaction transaction = block.Transactions[(int)positionIndex];
        RecoverTxSenderIfNeeded(transaction);

        TransactionForRpcContext extraData = new(
            chainId: _specProvider.ChainId,
            blockHash: block.Hash,
            blockNumber: block.Number,
            txIndex: (int)positionIndex,
            blockTimestamp: block.Timestamp,
            baseFee: block.BaseFeePerGas,
            receipt: null);
        TransactionForRpc transactionModel = TransactionForRpc.FromTransaction(transaction, extraData);
        return ResultWrapper<TransactionForRpc?>.Success(transactionModel);
    }

    public ResultWrapper<BlockForRpc?> eth_getUncleByBlockHashAndIndex(Hash256 blockHash, UInt256 positionIndex) => GetUncle(new BlockParameter(blockHash), positionIndex);

    public ResultWrapper<BlockForRpc?> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter,
        UInt256 positionIndex) => GetUncle(blockParameter, positionIndex);

    private ResultWrapper<BlockForRpc?> GetUncle(BlockParameter blockParameter, UInt256 positionIndex)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        if (searchResult.IsError)
        {
            return ResultWrapper<BlockForRpc?>.Success(null);
        }

        Block block = searchResult.Object!;
        if (positionIndex >= block.Uncles.Length)
        {
            return ResultWrapper<BlockForRpc?>.Success(null);
        }

        BlockHeader uncleHeader = block.Uncles[(int)positionIndex];
        return ResultWrapper<BlockForRpc?>.Success(_blockForRpcFactory.Create(new Block(uncleHeader), false, _specProvider));
    }

    public ResultWrapper<UInt256?> eth_newFilter(Filter filter)
    {
        BlockParameter fromBlock = filter.FromBlock;
        BlockParameter toBlock = filter.ToBlock;
        int filterId = _blockchainBridge.NewFilter(fromBlock, toBlock, filter.Address, filter.Topics);
        return ResultWrapper<UInt256?>.Success((UInt256)filterId);
    }

    public ResultWrapper<UInt256?> eth_newBlockFilter()
    {
        int filterId = _blockchainBridge.NewBlockFilter();
        return ResultWrapper<UInt256?>.Success((UInt256)filterId);
    }

    public ResultWrapper<UInt256?> eth_newPendingTransactionFilter()
    {
        int filterId = _blockchainBridge.NewPendingTransactionFilter();
        return ResultWrapper<UInt256?>.Success((UInt256)filterId);
    }

    public ResultWrapper<bool?> eth_uninstallFilter(UInt256 filterId)
    {
        // Nethermind uses int for filter IDs, which is okay, but other clients use UInt256.
        // If filterId is greater than int.MaxValue, it could not have been created by Nethermind.
        // In that case, return false instead of throwing an internal cast error.
        if (filterId > int.MaxValue)
            return ResultWrapper<bool?>.Success(false);

        _blockchainBridge.UninstallFilter((int)filterId);
        return ResultWrapper<bool?>.Success(true);
    }

    public ResultWrapper<IEnumerable<object>> eth_getFilterChanges(UInt256 filterId)
    {
        int id = filterId <= int.MaxValue ? (int)filterId : -1;
        FilterType filterType = _blockchainBridge.GetFilterType(id);
        switch (filterType)
        {
            case FilterType.BlockFilter:
                {
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge.GetBlockFilterChanges(id))
                        : ResultWrapper<IEnumerable<object>>.Fail("Filter not found", ErrorCodes.InvalidInput);
                }
            case FilterType.PendingTransactionFilter:
                {
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge.GetPendingTransactionFilterChanges(id))
                        : ResultWrapper<IEnumerable<object>>.Fail("Filter not found", ErrorCodes.InvalidInput);
                }
            case FilterType.LogFilter:
                {
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge.GetLogFilterChanges(id).ToArray())
                        : ResultWrapper<IEnumerable<object>>.Fail("Filter not found", ErrorCodes.InvalidInput);
                }
            default:
                {
                    return ResultWrapper<IEnumerable<object>>.Fail($"Filter type {filterType} is not supported.", ErrorCodes.InvalidInput);
                }
        }
    }

    public ResultWrapper<IEnumerable<FilterLog>> eth_getFilterLogs(UInt256 filterId)
    {
        CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        bool timeoutTransferred = false;

        try
        {
            int id = filterId <= int.MaxValue ? (int)filterId : -1;

            if (id >= 0 && _blockchainBridge.GetLogFilter(id) is { } logFilter)
            {
                SearchResult<BlockHeader> fromResult = _blockFinder.SearchForHeader(logFilter.FromBlock);
                SearchResult<BlockHeader> toResult = logFilter.FromBlock == logFilter.ToBlock
                    ? fromResult
                    : _blockFinder.SearchForHeader(logFilter.ToBlock);

                if (!fromResult.IsError && !toResult.IsError
                    && EnsureBlockRangeWithinLimit(fromResult.Object!, toResult.Object!, logFilter.UseIndex) is { } rangeError)
                    return rangeError;
            }

            bool filterFound = _blockchainBridge.TryGetLogs(id, out IEnumerable<FilterLog> filterLogs, cancellationToken);
            if (id < 0 || !filterFound)
            {
                return ResultWrapper<IEnumerable<FilterLog>>.Fail($"Filter with id: {filterId} does not exist.");
            }

            return GetLogsResponse(filterLogs, timeout, verifyLogsResponse: null, out timeoutTransferred);
        }
        catch (ResourceNotFoundException)
        {
            return ResultWrapper<IEnumerable<FilterLog>>.Fail(ErrorMessages.PrunedHistoryUnavailable, ErrorCodes.PrunedHistoryUnavailable, _ethSyncingInfo.SyncMode.HaveNotSyncedReceiptsYet());
        }
        finally
        {
            if (!timeoutTransferred)
            {
                timeout.Dispose();
            }
        }
    }

    public ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter)
    {
        BlockParameter fromBlock = filter.FromBlock!;
        BlockParameter toBlock = filter.ToBlock!;

        ResultWrapper<IEnumerable<FilterLog>> FailWithNoHeadersSyncedYet(SearchResult<BlockHeader> blockResult)
            => GetFailureResult<IEnumerable<FilterLog>, BlockHeader>(blockResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet());

        // because of lazy evaluation of enumerable, we need to do the validation here first
        CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        bool timeoutTransferred = false;
        try
        {
            CancellationToken cancellationToken = timeout.Token;

            ulong? headNumber = _blockFinder.Head?.Number;
            if (headNumber < fromBlock.BlockNumber || headNumber < toBlock.BlockNumber)
            {
                return ResultWrapper<IEnumerable<FilterLog>>.Fail("requested block range is in the future", ErrorCodes.InvalidParams);
            }
            if (fromBlock.BlockNumber > toBlock.BlockNumber)
            {
                return ResultWrapper<IEnumerable<FilterLog>>.Fail("invalid block range params", ErrorCodes.InvalidParams);
            }

            SearchResult<BlockHeader> fromResult = blockFinder.SearchForHeader(fromBlock);
            if (fromResult.IsError)
            {
                return FailWithNoHeadersSyncedYet(fromResult);
            }

            cancellationToken.ThrowIfCancellationRequested();

            SearchResult<BlockHeader> toResult;
            if (fromBlock == toBlock)
            {
                toResult = fromResult;
            }
            else
            {
                toResult = blockFinder.SearchForHeader(toBlock);
                if (toResult.IsError)
                {
                    return FailWithNoHeadersSyncedYet(toResult);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            BlockHeader fromBlockHeader = fromResult.Object!;
            BlockHeader toBlockHeader = toResult.Object!;

            if (EnsureBlockRangeWithinLimit(fromBlockHeader, toBlockHeader, filter.UseIndex) is { } rangeError)
                return rangeError;

            LogFilter logFilter = _blockchainBridge.GetFilter(fromBlock, toBlock, filter.Address, filter.Topics);

            // ReSharper disable once ConditionIsAlwaysTrueOrFalse - can be null in tests
            if (logFilter is not null && filter is not null)
                logFilter.UseIndex = filter.UseIndex;

            IEnumerable<FilterLog> filterLogs = _blockchainBridge.GetLogs(logFilter, fromBlockHeader, toBlockHeader, cancellationToken);

            bool verifyLogIndexResponse = logIndexConfig?.VerifyRpcResponse is true && logFilter.UseIndex;
            return GetLogsResponse(
                filterLogs,
                timeout,
                verifyLogIndexResponse ? (logs, token) => VerifyLogsResponse(logs, logFilter, fromBlockHeader, toBlockHeader, token) : null,
                out timeoutTransferred);
        }
        catch (ResourceNotFoundException)
        {
            return ResultWrapper<IEnumerable<FilterLog>>.Fail(ErrorMessages.PrunedHistoryUnavailable, ErrorCodes.PrunedHistoryUnavailable, _ethSyncingInfo.SyncMode.HaveNotSyncedReceiptsYet());
        }
        finally
        {
            if (!timeoutTransferred)
            {
                timeout.Dispose();
            }
        }
    }

    // https://github.com/ethereum/EIPs/issues/1186
    public ResultWrapper<AccountProof> eth_getProof(Address accountAddress, StorageKeys storageKeys, BlockParameter? blockParameter)
    {
        if (storageKeys.Count > GetProofStorageKeyLimit)
        {
            return ResultWrapper<AccountProof>.Fail(
                $"storageKeys: {storageKeys.Count} is over the query limit {GetProofStorageKeyLimit}.",
                ErrorCodes.InvalidParams);
        }

        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return GetFailureResult<AccountProof, BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet());
        }

        BlockHeader header = searchResult.Object;

        if (!_blockchainBridge.HasStateForBlock(header!))
        {
            return GetStateFailureResult<AccountProof>(header);
        }

        using CancellationTokenSource timeout = _rpcConfig.BuildTimeoutCancellationToken();
        AccountProofCollector accountProofCollector = new(accountAddress, storageKeys, timeout.Token);
        _blockchainBridge.RunTreeVisitor(accountProofCollector, header!);
        return ResultWrapper<AccountProof>.Success(accountProofCollector.BuildResult());
    }

    public ResultWrapper<ulong> eth_chainId()
    {
        ResultWrapper<ulong>? cachedResponse = Volatile.Read(ref _chainIdResponse);
        if (cachedResponse is not null)
        {
            return cachedResponse;
        }

        try
        {
            ulong chainId = _blockchainBridge.GetChainId();
            CachedChainIdResponse response = new(chainId);
            return Interlocked.CompareExchange(ref _chainIdResponse, response, null) ?? response;
        }
        catch (Exception ex)
        {
            return ResultWrapper<ulong>.Fail(ex.Message, ErrorCodes.InternalError, 0ul);
        }
    }

    private sealed class CachedChainIdResponse : ResultWrapper<ulong>, IJsonRpcRawResponse
    {
        private readonly byte[] _rawResult;

        public CachedChainIdResponse(ulong chainId)
        {
            Data = chainId;
            string hexChainId = chainId.ToHexString(skipLeadingZeros: true);
            byte[] rawResult = GC.AllocateUninitializedArray<byte>(hexChainId.Length + 2);
            rawResult[0] = (byte)'"';
            Encoding.ASCII.GetBytes(hexChainId, rawResult.AsSpan(1));
            rawResult[^1] = (byte)'"';
            _rawResult = rawResult;
        }

        void IJsonRpcRawResponse.WriteRaw(IBufferWriter<byte> writer) =>
            JsonRpcResponseWriter.WriteRawSuccess(writer, _rawResult, in IdRef);
    }

    protected void RecoverTxSenderIfNeeded(Transaction transaction) => transaction.SenderAddress ??= _blockchainBridge.RecoverTxSender(transaction);

    private ResultWrapper<IEnumerable<FilterLog>> GetLogsResponse(
        IEnumerable<FilterLog> filterLogs,
        CancellationTokenSource timeout,
        Action<IList<FilterLog>, CancellationToken>? verifyLogsResponse,
        out bool timeoutTransferred)
    {
        timeoutTransferred = false;
        bool enforceLogsLimits = JsonRpcContext.Current.Value?.IsAuthenticated != true;
        bool enforceMaxLogs = enforceLogsLimits && _rpcConfig.MaxLogsPerResponse != 0;

        if (_rpcConfig.EnableLogsStreamMode && verifyLogsResponse is null)
        {
            long? maxLogsResponseBodySize = enforceLogsLimits ? _rpcConfig.MaxLogsResponseBodySize : null;
            long? maxBatchResponseBodySize = enforceLogsLimits ? _rpcConfig.MaxBatchResponseBodySize : null;
            ResultWrapper<IEnumerable<FilterLog>> response = ResultWrapper<IEnumerable<FilterLog>>.Success(
                new LogsStreamableResult(filterLogs, _rpcConfig.MaxLogsPerResponse, enforceMaxLogs, maxLogsResponseBodySize, maxBatchResponseBodySize, timeout, _logger));
            timeoutTransferred = true;
            return response;
        }

        ArrayPoolList<FilterLog> logs = new(_rpcConfig.MaxLogsPerResponse);

        foreach (FilterLog log in filterLogs)
        {
            logs.Add(log);
            if (enforceMaxLogs && logs.Count > _rpcConfig.MaxLogsPerResponse)
            {
                logs.Dispose();
                return ResultWrapper<IEnumerable<FilterLog>>.Fail($"Too many logs requested. Max logs per response is {_rpcConfig.MaxLogsPerResponse}.", ErrorCodes.LimitExceeded);
            }
        }

        verifyLogsResponse?.Invoke(logs, timeout.Token);

        return ResultWrapper<IEnumerable<FilterLog>>.Success(logs);
    }

    public ResultWrapper<AccountForRpc?> eth_getAccount(Address accountAddress, BlockParameter? blockParameter)
    {
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return GetFailureResult<AccountForRpc?, BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet());
        }

        BlockHeader header = searchResult.Object!;
        if (!_blockchainBridge.HasStateForBlock(header))
            return GetStateFailureResult<AccountForRpc?>(header);
        return ResultWrapper<AccountForRpc?>.Success(
            _stateReader.TryGetAccount(header, accountAddress, out AccountStruct account)
                ? new AccountForRpc(account)
                : null);
    }

    public ResultWrapper<AccountInfoForRpc?> eth_getAccountInfo(Address accountAddress, BlockParameter? blockParameter)
    {
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return GetFailureResult<AccountInfoForRpc?, BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet());
        }

        BlockHeader header = searchResult.Object!;
        if (!_blockchainBridge.HasStateForBlock(header))
            return GetStateFailureResult<AccountInfoForRpc?>(header);
        return ResultWrapper<AccountInfoForRpc?>.Success(
            _stateReader.TryGetAccount(header, accountAddress, out AccountStruct account)
                ? new AccountInfoForRpc
                {
                    Balance = account.Balance,
                    Nonce = account.Nonce,
                    Code = _stateReader.GetCode(account.CodeHash) ?? []
                }
                : AccountInfoForRpc.Empty);
    }

    protected static ResultWrapper<TResult> GetFailureResult<TResult, TSearch>(SearchResult<TSearch> searchResult, bool isTemporary) where TSearch : class =>
        ResultWrapper<TResult>.Fail(searchResult, isTemporary && searchResult.ErrorCode == ErrorCodes.ResourceNotFound);

    private ResultWrapper<TResult> GetStateFailureResult<TResult>(BlockHeader header) =>
        ResultWrapper<TResult>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable, _ethSyncingInfo.SyncMode.HaveNotSyncedStateYet());

    public virtual ResultWrapper<ReceiptForRpc?> eth_getTransactionReceipt(Hash256 txHash)
    {
        (TxReceipt? receipt, ulong blockTimestamp, TxGasInfo? gasInfo, int logIndexStart) = _blockchainBridge.GetTxReceiptInfo(txHash);
        if (receipt is null || gasInfo is null)
        {
            return ResultWrapper<ReceiptForRpc>.Success(null);
        }

        if (_logger.IsTrace) _logger.Trace($"eth_getTransactionReceipt request {txHash}, result: {txHash}");
        return ResultWrapper<ReceiptForRpc>.Success(new(txHash, receipt, blockTimestamp, gasInfo.Value, logIndexStart));
    }

    public virtual ResultWrapper<ReceiptForRpc[]?> eth_getBlockReceipts(BlockParameter blockParameter)
    {
        SearchResult<Block> searchResult = blockFinder.SearchForBlock(blockParameter);
        return searchResult switch
        {
            { IsError: true } => ResultWrapper<ReceiptForRpc[]?>.Success(null),
            _ => _receiptFinder.GetBlockReceipts(blockParameter, _blockFinder, _specProvider)
        };
    }

    public ResultWrapper<JsonNode> eth_config()
    {
        ForkActivationsSummary forks = forkInfo.GetForkActivationsSummary(_blockFinder.Head?.Header);

        return ResultWrapper<JsonNode>.Success(JsonNode.Parse(JsonSerializer.Serialize((new ForkConfigSummary
        {
            Current = GetForkConfig(forks.Current, _specProvider)!,
            Next = GetForkConfig(forks.Next, _specProvider),
            Last = GetForkConfig(forks.Last, _specProvider)
        }), UnchangedDictionaryKeyOptions)));

        static ForkConfig? GetForkConfig(Fork? fork, ISpecProvider specProvider)
        {
            if (fork is null)
            {
                return null;
            }

            IReleaseSpec? spec = specProvider.GetSpec(fork.Value.Activation.BlockNumber, fork.Value.Activation.Timestamp);

            return new ForkConfig
            {
                ActivationTime = fork.Value.Activation.Timestamp is not null ? (int)fork.Value.Activation.Timestamp : null,
                ActivationBlock = fork.Value.Activation.Timestamp is null ? (int)fork.Value.Activation.BlockNumber : null,
                BlobSchedule = spec.IsEip4844Enabled ? new BlobScheduleSettingsForRpc
                {
                    BaseFeeUpdateFraction = (int)spec.BlobBaseFeeUpdateFraction,
                    Max = (int)spec.MaxBlobCount,
                    Target = (int)spec.TargetBlobCount,
                } : null,
                ChainId = specProvider.ChainId,
                ForkId = fork.Value.Id.HashBytes,
                Precompiles = spec.ListPrecompiles(),
                SystemContracts = spec.ListSystemContracts(),
            };
        }
    }

    public ResultWrapper<AccountAccessForRpc[]?> eth_getBlockAccessList(BlockParameter blockParameter)
    {
        // A pending block has no committed access list yet.
        if (blockParameter.Type == BlockParameterType.Pending)
        {
            return ResultWrapper<AccountAccessForRpc[]?>.Success(null);
        }

        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        if (searchResult.IsError)
        {
            // Unknown blocks yield null per execution-apis; pruned/non-canonical failures keep their error.
            return searchResult.Error == BlockFinderExtensions.HeaderNotFound
                ? ResultWrapper<AccountAccessForRpc[]?>.Success(null)
                : ResultWrapper<AccountAccessForRpc[]?>.Fail(searchResult);
        }

        Block block = searchResult.Object!;
        if (block.BlockAccessListHash is null)
        {
            // Pre-EIP-7928 block: the resource does not exist.
            return ResultWrapper<AccountAccessForRpc[]?>.Fail("Resource not found", ErrorCodes.BlockAccessListResourceNotFound);
        }

        ReadOnlyBlockAccessList? bal = blockchainBridge.GetBlockAccessList(block.Number, block.Hash!);

        return bal is null ?
            ResultWrapper<AccountAccessForRpc[]?>.Fail(ErrorMessages.PrunedHistoryUnavailable, ErrorCodes.PrunedHistoryUnavailable)
            : ResultWrapper<AccountAccessForRpc[]?>.Success(AccountAccessForRpc.FromBlockAccessList(bal));
    }

    public ResultWrapper<EthCapabilities> eth_capabilities() =>
        ResultWrapper<EthCapabilities>.Success(_capabilitiesProvider.GetCapabilities());

    private CancellationTokenSource BuildTimeoutCancellationTokenSource() =>
        _rpcConfig.BuildTimeoutCancellationToken();

    private void VerifyLogsResponse(IList<FilterLog> response, LogFilter filter, BlockHeader from, BlockHeader to, CancellationToken cancellation)
    {
        filter.UseIndex = false;
        IEnumerable<FilterLog>? expectedResponse = _blockchainBridge.GetLogs(filter, from, to, cancellation);

        using IEnumerator<FilterLog> expectedEnum = expectedResponse.GetEnumerator();

        int i = -1;
        while (++i < response.Count | expectedEnum.MoveNext())
        {
            FilterLog? actual = i < response.Count ? response[i] : null;
            FilterLog? expected = expectedEnum.Current;

            if ((actual?.BlockNumber, actual?.LogIndex) != (expected?.BlockNumber, expected?.LogIndex))
            {
                throw new LogIndexStateException(
                    $"Incorrect result from log index at position #{i}. " +
                    $"Expected: block {expected?.BlockNumber}, log #{expected?.LogIndex}. " +
                    $"Actual: block {actual?.BlockNumber}, log #{actual?.LogIndex}."
                );
            }
        }
    }

    // cap block range of a logs query against unbounded sequential scans, skip if log index is enabled
    private ResultWrapper<IEnumerable<FilterLog>>? EnsureBlockRangeWithinLimit(BlockHeader fromBlock, BlockHeader toBlock, bool useIndex)
    {
        int maxBlockDepth = _receiptConfig.MaxBlockDepth;
        bool usingLogIndex = logIndexConfig?.Enabled is true && useIndex;

        if (usingLogIndex || maxBlockDepth <= 0 || toBlock.Number < fromBlock.Number)
            return null;

        ulong rangeSize = toBlock.Number - fromBlock.Number + 1;
        if (rangeSize > (ulong)maxBlockDepth)
        {
            return ResultWrapper<IEnumerable<FilterLog>>.Fail(
                $"Block range {rangeSize} exceeds the maximum of {maxBlockDepth} blocks per logs request. " +
                $"Use a narrower fromBlock/toBlock range or increase Receipt.{nameof(IReceiptConfig.MaxBlockDepth)}.",
                ErrorCodes.InvalidParams);
        }

        return null;
    }
}
