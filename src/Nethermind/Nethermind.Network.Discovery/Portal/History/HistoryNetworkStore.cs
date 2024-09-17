// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal.History;

public class HistoryNetworkStore(IBlockTree blockTree, ILogManager logManager): IPortalContentNetwork.Store
{
    private readonly HistoryNetworkEncoderDecoder _encoderDecoder = new();
    private readonly ILogger _logger = logManager.GetClassLogger<HistoryNetworkStore>();

    public byte[]? GetContent(byte[] contentKey)
    {
        ContentKey key = SlowSSZ.Deserialize<ContentKey>(contentKey);

        if (key.HeaderKey != null)
        {
            BlockHeader? header = blockTree.FindHeader(key.HeaderKey!);
            if (header == null) return null;

            return _encoderDecoder.EncodeHeader(header!);
        }

        if (key.BodyKey != null)
        {
            Block? block = blockTree.FindBlock(key.BodyKey!);
            if (block == null) return null;

            return _encoderDecoder.EncodeBlockBody(block.Body!);
        }

        throw new Exception($"unsupported content {contentKey}");
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
    }
}
