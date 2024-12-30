// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization;

namespace Nethermind.Network.Portal.History;

public class HistoryNetworkStore(
    IBlockTree blockFinder,
    IReceiptFinder receiptFinder,
    ILogManager logManager
) : IPortalContentNetworkStore
{
    private readonly HistoryNetworkEncoderDecoder _encoderDecoder = new();
    private readonly ILogger _logger = logManager.GetClassLogger<HistoryNetworkStore>();

    private readonly ConcurrentDictionary<byte[], byte[]>.AlternateLookup<ReadOnlySpan<byte>> _testStore = new ConcurrentDictionary<byte[], byte[]>(Bytes.EqualityComparer).GetAlternateLookup<ReadOnlySpan<byte>>();

    public byte[]? GetContent(ReadOnlySpan<byte> contentKey)
    {
        if (_testStore.TryGetValue(contentKey, out var value))
        {
            _logger.Info($"Content {contentKey.ToHexString()} in cache of size {value.Length}");
            return value;
        }
        _logger.Info($"Content {contentKey.ToHexString()} not in cache, loading from db");

        SszEncoding.Decode(contentKey, out HistoryContentKey key);

        if (key.Selector == HistoryContentType.HeaderByHash)
        {
            BlockHeader? header = blockFinder.FindHeader(new Hash256(key.HeaderByHash));
            if (header == null) return null;

            return _encoderDecoder.EncodeHeader(header);
        }

        if (key.Selector == HistoryContentType.HeaderByBlockNumber)
        {
            BlockHeader? header = blockFinder.FindHeader((long)key.HeaderByBlockNumber, BlockTreeLookupOptions.None);
            if (header == null) return null;

            return _encoderDecoder.EncodeHeader(header);
        }

        if (key.Selector == HistoryContentType.BodyByHash)
        {
            Block? block = blockFinder.FindBlock(new Hash256(key.BodyByHash));
            if (block == null) return null;

            return _encoderDecoder.EncodeBlockBody(block.Body);
        }

        if (key.Selector == HistoryContentType.ReceiptByHash)
        {
            TxReceipt[]? receipts = receiptFinder.Get(new Hash256(key.ReceiptByHash));
            if (receipts == null) return null;

            return _encoderDecoder.Encode(receipts);
        }

        throw new Exception($"unsupported content {Convert.ToHexStringLower(contentKey)}");
    }

    public bool ShouldAcceptOffer(ReadOnlySpan<byte> offerContentKey)
    {
        // Note: Just testing
        return true;
    }

    public void Store(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> content)
    {
        // Note: Just testing
        _logger.Info($"Got content {contentKey.ToHexString()} of size {content.Length} from portal network");

        _testStore[contentKey] = content.ToArray();
    }
}
