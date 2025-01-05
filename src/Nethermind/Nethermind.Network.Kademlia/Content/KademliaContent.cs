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
        if (_logger.IsInfo) _logger.Info($"LA 1");
        var result = default(TContent);
        var resultWasFound = false;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = cts.Token;
        // TODO: Timeout?

        if (kademliaContentStore.TryGetValue(contentKey, out TContent? content))
            return content;

        if (_logger.IsInfo) _logger.Info($"LA 2");
        ValueHash256 targetHash = contentHashProvider.GetHash(contentKey);

        try
        {
            _ = await lookupAlgo.Lookup(
                targetHash, config.KSize, async (nextNode, token) =>
                {
                    try
                    {
                        if (_logger.IsInfo) _logger.Info($"LA 3");
                        FindValueResponse<TNode, TContent> valueResponse = await contentMessageSender.FindValue(nextNode, contentKey, token);

                        if (_logger.IsInfo) _logger.Info($"LA 4");
                        if (valueResponse.HasValue)
                        {
                            if (_logger.IsInfo) _logger.Info($"Value response has value {valueResponse.Value}");
                            resultWasFound = true;
                            result = valueResponse.Value; // Shortcut so that once it find the value, it should stop.
                            if (_logger.IsInfo) _logger.Info($"LA 5");
                            await cts.CancelAsync();
                        }

                        if (_logger.IsInfo) _logger.Info($"Value response has no value. Returning {valueResponse.Neighbours.Length} neighbours");
                        if (_logger.IsInfo) _logger.Info($"LA 6 {valueResponse.Neighbours.Length}");
                        return valueResponse.Neighbours;
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsInfo) _logger.Info($"LA 8 {e.Message} {e.StackTrace}");
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

        if (_logger.IsInfo) _logger.Info($"LA 7");
        return result;
    }

    public override string ToString()
    {
        return $"KademliaContent to string called from {new StackTrace()}";
    }
}
