// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Monitor;

/// <summary>
/// Caches recent blocks by number, populated from the head subscription, falling back to a node request for a missing block.
/// </summary>
internal class BlockProvider(RpcClient client, int capacity = 128)
{
    private readonly BlockInfo?[] _blocks = new BlockInfo?[capacity];
    private readonly Lock _lock = new();

    public void OnNewHead(BlockInfo block)
    {
        lock (_lock)
            _blocks[Index(block.Number)] = block;
    }

    public async Task<BlockInfo> GetAsync(long number, CancellationToken ct = default)
    {
        if (TryGet(number) is { } cached)
            return cached;

        BlockInfo fetched = await FetchAsync(number, ct);

        lock (_lock)
        {
            // don't clobber a fresher block the subscription may have stored meanwhile
            ref BlockInfo? slot = ref _blocks[Index(number)];
            if (slot is null || slot.Number < number)
                slot = fetched;
        }

        return fetched;
    }

    private BlockInfo? TryGet(long number)
    {
        lock (_lock)
        {
            BlockInfo? block = _blocks[Index(number)];
            return block?.Number == number ? block : null;
        }
    }

    private async Task<BlockInfo> FetchAsync(long number, CancellationToken ct)
    {
        JsonObject request = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "eth_getBlockByNumber",
            ["params"] = new JsonArray($"0x{number:x}", false)
        };

        JsonNode response = await client.RetrySendAsync(request, ct);

        if (response["error"] is { } error)
            throw new Exception($"Failed to fetch block #{number}: {error.ToCompactString()}");

        return response["result"] is { } result
            ? new BlockInfo(result)
            : throw new Exception($"Block #{number} not found");
    }

    private int Index(long number) => (int)(number % _blocks.Length);
}
