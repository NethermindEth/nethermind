// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

internal static class DiscoveryNodeRecord
{
    internal static NodeRecord GetOrSetState(Node node, ILogger logger, string protocol, Func<Node, NodeRecord, bool> isAcceptableRecord)
    {
        if (TryGet(node, logger, protocol, isAcceptableRecord, out NodeRecord? record))
        {
            return record;
        }

        record = new NodeRecord();
        node.EnrRecord = record;
        return record;
    }

    internal static bool TryGet(Node node, ILogger logger, string protocol, Func<Node, NodeRecord, bool> isAcceptableRecord, [NotNullWhen(true)] out NodeRecord? record)
    {
        if (node.EnrRecord is { Signature: not null } existingRecord)
        {
            record = existingRecord;
            return true;
        }

        if (!string.IsNullOrEmpty(node.Enr))
        {
            try
            {
                NodeRecord parsedRecord = NodeRecord.FromEnrString(node.Enr);
                if (isAcceptableRecord(node, parsedRecord))
                {
                    node.EnrRecord = parsedRecord;
                    record = parsedRecord;
                    return true;
                }
            }
            catch (Exception e)
            {
                if (logger.IsTrace) logger.Trace($"Unable to parse known {protocol} ENR for {node}: {e}");
            }
        }

        if (node.EnrRecord is not null)
        {
            record = node.EnrRecord;
            return true;
        }

        record = null;
        return false;
    }

    internal static void TransferRequest(NodeRecord source, NodeRecord target)
    {
        ulong requestingSequence = source.RequestingEnrSequence;
        if (requestingSequence > target.EnrSequence)
        {
            target.TryRequestEnrSequence(requestingSequence);
        }
    }
}
