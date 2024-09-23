// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Portal.Messages;
using Nethermind.Serialization;

namespace Nethermind.Network.Discovery.Portal.History;

public class HistoryNetworkStore(IBlockTree blockTree, ILogManager logManager): IPortalContentNetworkStore
{
    private readonly HistoryNetworkEncoderDecoder _encoderDecoder = new();
    private readonly ILogger _logger = logManager.GetClassLogger<HistoryNetworkStore>();

    private readonly SpanConcurrentDictionary<byte, byte[]> _testStore = new(Bytes.SpanEqualityComparer);

    public byte[]? GetContent(byte[] contentKey)
    {
        if (_testStore.TryGetValue(contentKey, out byte[]? value))
        {
            _logger.Info($"Content {contentKey.ToHexString()} in test store of size {value.Length}");
            return value;
        }
        _logger.Info($"Content {contentKey.ToHexString()} not in test store");

        SszEncoding.Decode(contentKey, out HistoryContentKey key);

        if (key.Selector == HistoryContentType.HeaderByHash)
        {
            BlockHeader? header = blockTree.FindHeader(new Hash256(key.HeaderByHash));
            if (header == null) return null;

            return _encoderDecoder.EncodeHeader(header!);
        }

        if (key.Selector == HistoryContentType.BodyByHash)
        {
            Block? block = blockTree.FindBlock(new Hash256(key.BodyByHash));
            if (block == null) return null;

            return _encoderDecoder.EncodeBlockBody(block.Body!);
        }

        // throw new Exception($"unsupported content {contentKey}");
        return null;
    }

    public bool ShouldAcceptOffer(byte[] offerContentKey)
    {
        // Note: Just testing
        return true;
    }

    public void Store(byte[] contentKey, byte[] content)
    {
        // Note: Just testing
        _logger.Info($"Got content {contentKey.ToHexString()} of size {content.Length} from portal network");

        _testStore[contentKey] = content;
    }
}
