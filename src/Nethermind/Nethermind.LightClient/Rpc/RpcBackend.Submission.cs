// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc;

namespace Nethermind.LightClient.Rpc;

internal sealed partial class RpcBackend
{
    private readonly SemaphoreSlim _sendReceiptRequests = new(8, 8);

    internal async ValueTask<JsonElement> GetPendingTransactionViewAsync(Transaction tx, ulong secondsPerSlot, CancellationToken token)
    {
        TransactionForRpcContext context = new(chainId);
        if (tx.SupportsFrames && specProvider is not null && getLatestHead?.Invoke() is { } head)
        {
            BlockHeader header = await execution.GetHeaderAsync(head, token);
            context = TransactionForRpcContext.ForPending(specProvider, header, secondsPerSlot);
        }
        return BoundedJson.ToElement(TransactionForRpc.FromTransaction(tx, context));
    }

    internal async Task<JsonElement> SendRawTransactionSyncAsync(byte[] raw, ulong? timeoutMs, CancellationToken cancellationToken)
    {
        if (_data is null) throw new RpcException(-32601, "Verified transaction relay is unavailable.");
        if (raw.Length > 2 * 1024 * 1024) throw InvalidParameters();
        if (!_sendReceiptRequests.Wait(0, cancellationToken)) throw new RpcException(-32005, "Too many synchronous transaction requests.");
        try
        {
            VerifiedHead start = await LatestHeadAsync(cancellationToken);
            Hash256 hash = await _data.SendRawTransactionAsync(start, raw, calls, cancellationToken, requireReady: true);
            int waitMs = timeoutMs is > 0 ? (int)Math.Min(timeoutMs.Value, 60_000UL) : 20_000;
            using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(waitMs), timeProvider ?? TimeProvider.System);
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try { return await WaitForReceiptAsync(_data, start, hash, wait.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RpcException(ErrorCodes.TxSyncTimeout,
                    $"Transaction was submitted but an authenticated receipt did not arrive within {waitMs}ms.", hash.ToString());
            }
        }
        finally { _sendReceiptRequests.Release(); }
    }

    private async Task<JsonElement> WaitForReceiptAsync(ExecutionData data, VerifiedHead start, Hash256 hash, CancellationToken token)
    {
        Hash256 scannedTip = start.BlockHash;
        HashSet<Hash256> noMatch = [];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task? nextHead = getNextHeadChange?.Invoke();
            bool retry = false;
            if (getLatestHead?.Invoke() is { } head && head.BlockHash != scannedTip)
            {
                try
                {
                    using DataRequest admission = new(token);
                    BlockHeader newest = await execution.GetHeaderAsync(head, token);
                    ulong first = Math.Min(start.Number, head.Number);
                    int count = (int)Math.Min(257, head.Number - first + 1);
                    BlockHeader[] headers = await execution.GetRecentHeadersAsync(newest, count, token);
                    if (headers[0].Hash != start.BlockHash && head.Number - first < 256)
                    {
                        // The submission head was replaced. Search the bounded reorg window for inclusion.
                        first = first > RpcSubscriptions.MaxHistory ? first - RpcSubscriptions.MaxHistory : 0;
                        count = (int)Math.Min(257, head.Number - first + 1);
                        headers = await execution.GetRecentHeadersAsync(newest, count, token);
                    }
                    for (int i = headers.Length - 1; i >= 0; i--)
                    {
                        BlockHeader header = headers[i];
                        if (header.Hash == start.BlockHash || noMatch.Contains(header.Hash!)) continue;
                        if (await data.FindTransactionAsync(header, hash, ExecutionData.TransactionResult.Receipt, token) is { } receipt)
                        {
                            if (getLatestHead?.Invoke()?.BlockHash == head.BlockHash) return receipt;
                            retry = true;
                            break;
                        }
                        if (noMatch.Count == 256) noMatch.Clear();
                        noMatch.Add(header.Hash!);
                    }
                    if (!retry) scannedTip = head.BlockHash;
                }
                catch (ExecutionDataUnavailableException) { retry = true; }
                catch (RpcException exception) when (exception.Code is -32000 or -32001 or -32005) { retry = true; }
            }
            if (retry || nextHead is null)
                await Task.Delay(TimeSpan.FromMilliseconds(250), token);
            else
                await nextHead.WaitAsync(token);
        }
    }
}
