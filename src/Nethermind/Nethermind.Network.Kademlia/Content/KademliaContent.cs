// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Logging;
using System.Diagnostics;

namespace Nethermind.Network.Kademlia.Content;

public class KademliaContent<TNode, TContentKey, TContent>(
    IContentHashProvider<TContentKey> contentHashProvider,
    IKademliaContentStore<TContentKey, TContent> kademliaContentStore,
    IContentMessageSender<TNode, TContentKey, TContent> contentMessageSender,
    ILookupAlgo<TNode> lookupAlgo,
    KademliaConfig<TNode> config,
    ILogManager logManager
    ) : IKademliaContent<TContentKey, TContent> where TNode : notnull
{
    private readonly ILogger _logger = logManager.GetClassLogger<KademliaContent<TNode, TContentKey, TContent>>();

    public async Task<TContent?> LookupValue(TContentKey contentKey, CancellationToken token)
    {
        if (_logger.IsInfo) _logger.Info($"LA 1 {contentKey}");
        var result = default(TContent);
        var resultWasFound = false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = cts.Token;
        // TODO: Timeout?

        if (kademliaContentStore.TryGetValue(contentKey, out TContent? content))
            return content;

        if (_logger.IsInfo) _logger.Info($"LA 2 {contentKey}");
        ValueHash256 targetHash = contentHashProvider.GetHash(contentKey);

        try
        {
            _ = await lookupAlgo.Lookup(
                targetHash, config.KSize, async (nextNode, token) =>
                {
                    try
                    {
                        if (_logger.IsInfo) _logger.Info($"LA 3 {Convert.ToHexString((byte[])(object)contentKey!)} {nextNode:a/i}");
                        //var to = new CancellationTokenSource(); to.CancelAfter(3); 
                        //FindValueResponse<TNode, TContent> valueResponse = await contentMessageSender.FindValue(nextNode, contentKey, CancellationTokenSource.CreateLinkedTokenSource(token, to.Token).Token);
                        FindValueResponse<TNode, TContent> valueResponse = await contentMessageSender.FindValue(nextNode, contentKey, token);

                        if (_logger.IsInfo) _logger.Info($"LA 4 {Convert.ToHexString((byte[])(object)contentKey!)} {nextNode:a/i}");
                        if (valueResponse.HasValue)
                        {
                            if (_logger.IsInfo) _logger.Info($"Value response has value {valueResponse.Value}");
                            resultWasFound = true;
                            result = valueResponse.Value; // Shortcut so that once it find the value, it should stop.
                            if (_logger.IsInfo) _logger.Info($"LA 5 {Convert.ToHexString((byte[])(object)contentKey!)} {nextNode:a/i}");
                            await cts.CancelAsync();
                        }

                        if (_logger.IsInfo) _logger.Info($"Value response has no value. Returning {valueResponse.Neighbours.Length} neighbours");
                        if (_logger.IsInfo) _logger.Info($"LA 6 {Convert.ToHexString((byte[])(object)contentKey!)} {nextNode:a/i} {valueResponse.Neighbours.Length}: {string.Join(",", valueResponse.Neighbours.Select(n => $"{n:a/i}"))}");
                        return valueResponse.Neighbours;
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsInfo) _logger.Info($"LA 8 {Convert.ToHexString((byte[])(object)contentKey!)} {nextNode:a/i} {e.Message} {e.StackTrace}");
                        throw;
                    }
                },
                token
            );
        }
        catch (OperationCanceledException)
        {
            if (!resultWasFound) throw;
        }

        if (_logger.IsInfo) _logger.Info($"LA 9");
        return result;
    }

    public override string ToString()
    {
        return $"KademliaContent to string called from {new StackTrace()}";
    }
}
