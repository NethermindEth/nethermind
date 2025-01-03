// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nethermind.Network.Portal.History;

public class PortalHistoryNetwork : IPortalHistoryNetwork
{
    private readonly IPortalContentNetwork _contentNetwork;
    private readonly HistoryNetworkEncoderDecoder _encoderDecoder = new();
    private readonly IBlockTree _blockTree;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ISyncConfig syncConfig;
    private readonly RadiusTracker _radiusTracker;
    private readonly ILogger _logger;
    public static PortalHistoryNetwork? Current { get; private set; }

    public PortalHistoryNetwork(
        IPortalContentNetwork portalContentNetwork,
        IBlockTree blockTree,
        RadiusTracker radiusTracker,
        IReceiptStorage receiptStorage,
        ILogManager logManager,
        ISyncConfig syncConfig
    )
    {
        Current = this;

        _contentNetwork = portalContentNetwork;
        _logger = logManager.GetClassLogger<PortalHistoryNetwork>();
        _blockTree = blockTree;
        _receiptStorage = receiptStorage;
        this.syncConfig = syncConfig;
        _radiusTracker = radiusTracker;

        _ = Task.Run(HandleNewHeaders);
    }

    private SemaphoreSlim a = new(5, 5);

    private async Task HandleNewHeaders()
    {
        foreach (var header in HeadersToHandle.GetConsumingEnumerable())
        {
            await a.WaitAsync();
            _ = Load(header);
        }
    }

    public BlockingCollection<BlockHeader> HeadersToHandle = [];

    public void OnNewHeader(BlockHeader newHeader)
    {
        if (syncConfig.AncientReceiptsBarrierCalc > newHeader.Number || syncConfig.AncientBodiesBarrierCalc > newHeader.Number)
        {
            _logger.Warn($"Portal: skipped as pre-merge {newHeader.Number}");
            return;
        }
        HeadersToHandle.Add(newHeader);
    }

    private async Task Load(BlockHeader newHeader)
    {
        _logger.Warn($"Portal: requested {newHeader.Number}");

        try
        {
            if (newHeader.Hash is null)
            {
                return;
            }

            if (_radiusTracker is null)
            {
                return;
            }

            if (!_radiusTracker.IsContentInRadius(SszEncoding.Encode(new HistoryContentKey()
            {
                Selector = HistoryContentType.HeaderByHash,
                HeaderByHash = newHeader.Hash.Bytes.ToArray()
            })))
            {
                _logger.Warn($"Portal: skipped {newHeader.Number}, not in radius");
                return;
            }

            byte[] blockKey = SszEncoding.Encode(new HistoryContentKey()
            {
                Selector = HistoryContentType.BodyByHash,
                BodyByHash = newHeader.Hash.Bytes.ToArray()
            });

            Stopwatch sw = Stopwatch.StartNew();
            BlockBody? body = await LookupBlockBody(newHeader.Hash, CancellationToken.None);
            sw.Stop();
            Stopwatch sw2 = Stopwatch.StartNew();
            TxReceipt[]? receipts = await LookupReceipts(newHeader.Hash, CancellationToken.None);
            sw2.Stop();

            if (body is not null && receipts is not null)
            {
                Block block = new Block(newHeader, body);
                _blockTree.Insert(block, BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks, bodiesWriteFlags: WriteFlags.DisableWAL);
                _receiptStorage.Insert(block, receipts, true, writeFlags: WriteFlags.DisableWAL);
                _logger.Warn($"Portal: loaded {newHeader.Number}) {sw.Elapsed} {sw2.Elapsed}");
            }
            else
            {
                HeadersToHandle.Add(newHeader);
                _logger.Warn($"Portal: skipped {newHeader.Number}, not found ({body is not null} {receipts is not null})");
            }
        }
        catch
        {
            HeadersToHandle.Add(newHeader);
        }
        finally
        {
            a.Release();
        }
    }

    public async Task<BlockHeader?> LookupBlockHeader(ValueHash256 hash, CancellationToken token)
    {
        _logger.Info($"Looking up header {hash}");

        var asBytes = await _contentNetwork.LookupContent(SszEncoding.Encode(new HistoryContentKey()
        {
            Selector = HistoryContentType.HeaderByHash,
            HeaderByHash = hash.ToByteArray()
        }), token);

        return asBytes == null ? null : _encoderDecoder.DecodeHeader(asBytes!);
    }

    public async Task<BlockHeader?> LookupBlockHeader(ulong blockNumber, CancellationToken token)
    {
        _logger.Info($"Looking up header {blockNumber}");

        var asBytes = await _contentNetwork.LookupContent(SszEncoding.Encode(new HistoryContentKey()
        {
            Selector = HistoryContentType.HeaderByBlockNumber,
            HeaderByBlockNumber = blockNumber
        }), token);

        return asBytes == null ? null : _encoderDecoder.DecodeHeader(asBytes!);
    }

    public async Task<BlockBody?> LookupBlockBody(ValueHash256 hash, CancellationToken token)
    {
        _logger.Info($"Looking up body {hash}");

        var asBytes = await _contentNetwork.LookupContent(SszEncoding.Encode(new HistoryContentKey()
        {
            Selector = HistoryContentType.BodyByHash,
            BodyByHash = hash.ToByteArray()
        }), token);

        return asBytes == null ? null : _encoderDecoder.DecodeBody(asBytes!);
    }

    public async Task<BlockBody?> LookupBlockBodyFrom(IEnr enr, ValueHash256 hash, CancellationToken token)
    {
        _logger.Info($"Looking up body {hash}");

        var asBytes = await _contentNetwork.LookupContentFrom(enr, SszEncoding.Encode(new HistoryContentKey()
        {
            Selector = HistoryContentType.BodyByHash,
            BodyByHash = hash.ToByteArray()
        }), token);

        return asBytes == null ? null : _encoderDecoder.DecodeBody(asBytes!);
    }

    public async Task<TxReceipt[]?> LookupReceipts(ValueHash256 hash, CancellationToken token)
    {
        _logger.Info($"Looking up receipt {hash}");

        var asBytes = await _contentNetwork.LookupContent(SszEncoding.Encode(new HistoryContentKey()
        {
            Selector = HistoryContentType.ReceiptByHash,
            ReceiptByHash = hash.ToByteArray()
        }), token);

        return asBytes == null ? null : _encoderDecoder.DecodeReceipt(asBytes!);
    }

    public async Task Run(CancellationToken token)
    {
        _logger.Info("Running portal history network. Bootstrapping.");

        // You can skip bootstrap for testing, but the lookup time is going to be less realistic.
        await _contentNetwork.Bootstrap(token);

        // EnrEntryRegistry registry = new EnrEntryRegistry();
        // EnrFactory enrFactory = new(registry);
        // IdentityVerifierV4 identityVerifier = new();
        // var enr = enrFactory.CreateFromString("enr:-IS4QIvH4sUnNXGBWyR2M8GUb9B0haxCbqYZgC_9HYgvR890B8t3u44EeJpRA7czZBgDVzAovXEwx_F56YLU9ZoIhRAhgmlkgnY0gmlwhMIhK1qJc2VjcDI1NmsxoQKKqT_1W3phl5Ial-DBViE0MIZbwAHdRyrpZWKe0ttv4oN1ZHCCI4w", identityVerifier);
        // _logger.Info("-------------- looking up ----------------------");
        // BlockBody? body = await LookupBlockBody(new ValueHash256("0xead3ee2e6370d110e02840d700097d844ca4d1f62697194564f687985dfe2c1a"), token);
        // _logger.Info($"Lookup body got {body}");

        // await _contentNetwork.Run(token);
    }
}
