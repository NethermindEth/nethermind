// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using ModelContextProtocol.Server;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Dto;

namespace Nethermind.Mcp.Tools;

[McpServerToolType]
public sealed class ChainQueryTools(INethermindNodeAdapter adapter)
{
    [McpServerTool(Name = "get_block"), Description("Retrieve a block by 'latest', a number, or a 0x-prefixed hash.")]
    public BlockSummaryDto? GetBlock(
        [Description("Block identifier: 'latest', a decimal block number, or a 0x-prefixed 32-byte hash.")]
        string blockId = "latest")
    {
        BlockParameter parameter = ParseBlockId(blockId);
        return adapter.GetBlock(parameter);
    }

    private static BlockParameter ParseBlockId(string id)
    {
        if (string.Equals(id, "latest", StringComparison.OrdinalIgnoreCase)) return BlockParameter.Latest;
        if (string.Equals(id, "earliest", StringComparison.OrdinalIgnoreCase)) return BlockParameter.Earliest;
        if (string.Equals(id, "pending", StringComparison.OrdinalIgnoreCase)) return BlockParameter.Pending;
        if (string.Equals(id, "finalized", StringComparison.OrdinalIgnoreCase)) return BlockParameter.Finalized;
        if (string.Equals(id, "safe", StringComparison.OrdinalIgnoreCase)) return BlockParameter.Safe;

        if (id.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && id.Length == 66)
        {
            return new BlockParameter(new Hash256(id));
        }

        if (long.TryParse(id, out long number))
        {
            return new BlockParameter(number);
        }

        throw new ArgumentException($"Unrecognized block id: {id}", nameof(id));
    }
}
