// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Network.Enr;
using System.Buffers.Binary;
using EnrForkId = Nethermind.Network.Enr.ForkId;
using NetworkForkId = Nethermind.Network.ForkId;

namespace Nethermind.Network.Discovery;

public sealed class EnrForkIdFilter(IBlockTree blockTree, IForkInfo forkInfo, ILogManager logManager) : IEnrForkIdFilter
{
    private readonly ILogger _logger = logManager.GetClassLogger<EnrForkIdFilter>();

    public bool IsAcceptable(NodeRecord record)
    {
        if (!record.HasEntry(EnrContentKey.Eth))
        {
            if (_logger.IsTrace) _logger.Trace("ENR declined, missing eth fork ID entry.");
            return false;
        }

        if (!TryGetForkId(record, out NetworkForkId forkId))
        {
            return false;
        }

        ValidationResult result = forkInfo.ValidateForkId(forkId, blockTree.Head?.Header);
        if (result == ValidationResult.Valid)
        {
            return true;
        }

        if (_logger.IsTrace) _logger.Trace($"ENR declined, incompatible fork ID: {forkId}, validation result: {result}.");
        return false;
    }

    private bool TryGetForkId(NodeRecord record, out NetworkForkId forkId)
    {
        forkId = default;

        try
        {
            EnrForkId? enrForkId = record.GetValue<EnrForkId>(EnrContentKey.Eth);
            if (enrForkId is null ||
                enrForkId.Value.ForkHash.Length != sizeof(uint))
            {
                if (_logger.IsTrace) _logger.Trace("ENR declined, invalid eth fork ID entry.");
                return false;
            }

            uint forkHash = BinaryPrimitives.ReadUInt32BigEndian(enrForkId.Value.ForkHash);
            forkId = new NetworkForkId(forkHash, enrForkId.Value.Next);
            return true;
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"ENR declined, unable to parse eth fork ID entry: {e}");
            return false;
        }
    }
}
