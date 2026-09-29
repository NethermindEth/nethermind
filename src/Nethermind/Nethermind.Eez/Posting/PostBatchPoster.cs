// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Sequencer;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Eez.Posting;

/// <summary>A signed <c>postAndVerifyBatch</c> transaction, ready to submit.</summary>
public sealed record SignedPostBatch(byte[] Raw, Hash256 Hash, ulong Nonce);

public enum PostOutcome
{
    /// <summary>The batch landed and L1 now stores the Sync block it settles.</summary>
    Settled,

    /// <summary>The batch did not land, or landed without settling the Sync block.</summary>
    Failed,

    /// <summary>The batch's pinned L1 block was built at another timestamp, so the slot was never there to land in.</summary>
    SlotSkipped,
}

/// <summary>What became of a batch, and the L1 block it landed in or missed.</summary>
public readonly record struct PostResult(PostOutcome Outcome, ulong L1Block);

/// <summary>Signs, submits and follows the batches the sequencer posts to L1.</summary>
public interface IPostBatchPoster
{
    /// <summary>Signs with the poster's confirmed nonce, so a batch that failed in the mempool is replaced, not queued behind.</summary>
    /// <exception cref="L1SourceIncompleteException">L1 does not serve the poster's nonce or the base fee yet.</exception>
    Task<SignedPostBatch> Sign(byte[] calldata, CancellationToken token);

    /// <param name="bundle">The postBatch first, then any L1 transactions that must land with it, in order.</param>
    /// <returns>The L1 block the bundle targets.</returns>
    Task<ulong> Submit(IReadOnlyList<byte[]> bundle, BundleTarget target, CancellationToken token);

    /// <summary>Waits until the batch lands, or its target block passes without it.</summary>
    Task<PostResult> Observe(Hash256 postBatch, ulong targetBlock, BundleTarget target, ValueHash256 settles, CancellationToken token);
}

/// <summary>
/// Posts batches the way the reference composer does. The transaction is an EIP-1559 call to the registry with the
/// poster's pending nonce, a gas limit of the whole postBatch budget and a fee cap of twice the base fee plus the tip.
/// A bundle goes to the builder pinned to its block and timestamp, so it lands whole in its slot or not at all; a
/// builder without <c>eth_sendBundle</c> falls back to sending each transaction to the mempool, in order, which loses
/// that atomicity. A batch counts as settled only when L1 stores the Sync block it settles, whatever the receipt says.
/// </summary>
public sealed class PostBatchPoster(
    IL1PostingApi posting,
    IEezL1Api l1,
    ITxSigner signer,
    PostingSettings settings,
    ILogManager logManager) : IPostBatchPoster
{
    private const int MethodNotFound = -32601;
    private const ulong NextBlockSlack = 2;

    /// <summary>How much a replacement must pay over the transaction it replaces, above the mempool's 10% minimum.</summary>
    private const ulong ReplacementBumpPercent = 115;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILogger _logger = logManager.GetClassLogger<PostBatchPoster>();
    private readonly Hash256 _rollupTopic = L1BatchScanner.RollupTopic(settings.RollupId);
    private bool _bundlesUnsupported = !settings.HasBuilder;
    private (ulong Nonce, UInt256 Tip, UInt256 MaxFee)? _lastSigned;

    public async Task<SignedPostBatch> Sign(byte[] calldata, CancellationToken token)
    {
        Task<ulong?> nonceRead = posting.GetNonce(settings.Poster, token);
        Task<EezL1Block?> latestRead = l1.GetLatestBlock(token);
        await Task.WhenAll(nonceRead, latestRead);
        ulong nonce = nonceRead.Result ?? throw new L1SourceIncompleteException(0, $"L1 does not serve the nonce of poster {settings.Poster}.");
        UInt256 baseFee = latestRead.Result?.BaseFeePerGas ?? throw new L1SourceIncompleteException(0, "L1 does not serve the latest base fee.");
        (UInt256 tip, UInt256 maxFee) = Fees(nonce, baseFee * 2 + settings.PriorityFee);
        Transaction transaction = new()
        {
            Type = TxType.EIP1559,
            ChainId = settings.ChainId,
            Nonce = nonce,
            To = settings.Registry,
            Value = UInt256.Zero,
            Data = calldata,
            GasLimit = settings.GasLimit,
            GasPrice = tip,
            DecodedMaxFeePerGas = maxFee,
            SenderAddress = settings.Poster,
        };
        if (!signer.TrySign(transaction))
        {
            throw new EezSequencerException($"The poster key {settings.Poster} did not sign the batch.");
        }

        _lastSigned = (nonce, tip, maxFee);
        byte[] raw = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;
        return new SignedPostBatch(raw, Keccak.Compute(raw), nonce);
    }

    /// <summary>
    /// The fees of a batch at <paramref name="nonce"/>. A batch that failed in the mempool may still be pending at the
    /// same nonce, so a new one there pays enough more to replace it.
    /// </summary>
    private (UInt256 Tip, UInt256 MaxFee) Fees(ulong nonce, UInt256 maxFee)
    {
        UInt256 tip = settings.PriorityFee;
        if (_lastSigned is { } last && last.Nonce == nonce)
        {
            tip = UInt256.Max(tip, Bumped(last.Tip));
            maxFee = UInt256.Max(maxFee, Bumped(last.MaxFee));
        }

        return (tip, UInt256.Max(maxFee, tip));
    }

    private static UInt256 Bumped(in UInt256 fee) => fee * ReplacementBumpPercent / 100 + 1;

    public async Task<ulong> Submit(IReadOnlyList<byte[]> bundle, BundleTarget target, CancellationToken token)
    {
        ulong block = target.IsPinned
            ? target.Block
            : ((await l1.GetLatestBlock(token))?.Number ?? throw new L1SourceIncompleteException(0, "L1 does not report its latest block.")) + NextBlockSlack;
        if (!_bundlesUnsupported)
        {
            RpcAnswer<object> answer = await posting.SendBundle(bundle, block, target, token);
            if (!answer.IsError)
            {
                return block;
            }

            if (answer.ErrorCode != MethodNotFound)
            {
                throw new PostException($"The builder refused the bundle for L1 block {block}: {answer.ErrorMessage}");
            }

            _bundlesUnsupported = true;
            if (_logger.IsWarn) _logger.Warn("The builder does not serve eth_sendBundle; batches go to the mempool, without atomicity or a slot pin.");
        }

        for (int i = 0; i < bundle.Count; i++)
        {
            RpcAnswer<Hash256> sent = await posting.SendRawTransaction(bundle[i], token);
            if (sent.IsError && i == 0)
            {
                throw new PostException($"L1 refused the postBatch transaction: {sent.ErrorMessage}");
            }

            if (sent.IsError && _logger.IsWarn) _logger.Warn($"L1 refused transaction {i} of the bundle for block {block}: {sent.ErrorMessage}");
        }

        return block;
    }

    public async Task<PostResult> Observe(Hash256 postBatch, ulong targetBlock, BundleTarget target, ValueHash256 settles, CancellationToken token)
    {
        while (true)
        {
            try
            {
                if (await posting.GetReceipt(postBatch, token) is { } receipt)
                {
                    bool settled = receipt.Status == 1 && await Settled(receipt, settles, token);
                    return new PostResult(settled ? PostOutcome.Settled : PostOutcome.Failed, receipt.BlockNumber);
                }

                if (await Missed(postBatch, targetBlock, target, token) is { } missed)
                {
                    return new PostResult(missed, targetBlock);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                if (_logger.IsDebug) _logger.Debug($"Reading what became of batch {postBatch} failed; reading again: {e.Message}");
            }

            await Task.Delay(PollInterval, token);
        }
    }

    /// <summary>Whether L1 stored <paramref name="settles"/> in the block the batch landed in.</summary>
    private async Task<bool> Settled(EezL1Receipt receipt, ValueHash256 settles, CancellationToken token)
    {
        EezL1Log[] logs = await l1.GetLogs(settings.Registry, L1BatchScanner.L2ExecutionPerformedTopic, _rollupTopic, receipt.BlockNumber, receipt.BlockNumber, token)
            ?? throw new L1SourceIncompleteException(receipt.BlockNumber, $"L1 does not serve the logs of block {receipt.BlockNumber}.");
        foreach (EezL1Log log in logs)
        {
            if (log.BlockHash == receipt.BlockHash && log.Data is { Length: 32 } && new ValueHash256(log.Data) == settles)
            {
                return true;
            }
        }

        return false;
    }

    /// <returns>How the batch missed its block, or <see langword="null"/> while it may still land.</returns>
    private async Task<PostOutcome?> Missed(Hash256 postBatch, ulong targetBlock, BundleTarget target, CancellationToken token)
    {
        if (!target.IsPinned)
        {
            EezL1Block? latest = await l1.GetLatestBlock(token);
            return latest?.Number > targetBlock ? PostOutcome.Failed : null;
        }

        if (await l1.GetBlockByNumber(targetBlock, token) is not { } block || Array.IndexOf(block.Transactions ?? [], postBatch) >= 0)
        {
            return null;
        }

        return block.Timestamp == target.Timestamp ? PostOutcome.Failed : PostOutcome.SlotSkipped;
    }
}

/// <summary>How the sequencer posts its batches.</summary>
/// <param name="HasBuilder">Whether a builder RPC takes bundles; without one, batches go to the mempool.</param>
public sealed record PostingSettings(ulong ChainId, Address Registry, ulong RollupId, Address Poster, UInt256 PriorityFee, ulong GasLimit, bool HasBuilder);

/// <summary>L1 refused a batch transaction outright.</summary>
public sealed class PostException(string message) : Exception(message);
