// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Client;

namespace Nethermind.Eez.Follower;

/// <summary>The sequencer reads the follower makes to take its latest blocks as the unsafe head.</summary>
public interface IEezSequencerApi
{
    Task<EezL1Block?> GetLatestBlock(CancellationToken token);

    /// <returns>The RLP of the block, or <see langword="null"/> when the sequencer does not serve it.</returns>
    Task<byte[]?> GetRawBlock(Hash256 hash, CancellationToken token);
}

/// <remarks>Does not own <paramref name="rpcClient"/>; the plugin manages its lifetime.</remarks>
public sealed class EezSequencerApi(IJsonRpcClient rpcClient) : IEezSequencerApi
{
    public Task<EezL1Block?> GetLatestBlock(CancellationToken token) => rpcClient.Post<EezL1Block?>("eth_getBlockByNumber", "latest", false).WaitAsync(token);

    public Task<byte[]?> GetRawBlock(Hash256 hash, CancellationToken token) => rpcClient.Post<byte[]?>("debug_getRawBlock", hash.ToString()).WaitAsync(token);
}
