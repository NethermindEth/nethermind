// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Kademlia.Content;

public class KademliaContent<TNode, TContentKey, TContent>(
    IContentHashProvider<TContentKey> contentHashProvider,
    IKademliaContentStore<TContentKey, TContent> kademliaContentStore,
    IContentMessageSender<TNode, TContentKey, TContent> contentMessageSender,
    ILookupAlgo<TNode> lookupAlgo,
    KademliaConfig<TNode> config,
    ILogManager logManager
    ): IKademliaContent<TContentKey, TContent> where TNode : notnull
{
    private readonly ILogger _logger = logManager.GetClassLogger<KademliaContent<TNode, TContentKey, TContent>>();

    public async Task<TContent?> LookupValue(TContentKey contentKey, CancellationToken token)
    {
        TContent? result = default(TContent);
        bool resultWasFound = false;

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = cts.Token;
        // TODO: Timeout?

        if (kademliaContentStore.TryGetValue(contentKey, out TContent? content))
        {
            return content;
        }

        ValueHash256 targetHash = contentHashProvider.GetHash(contentKey);

        try
        {
            await lookupAlgo.Lookup(
                targetHash, config.KSize, async (nextNode, token) =>
                {
                    FindValueResponse<TNode, TContent> valueResponse = await contentMessageSender.FindValue(nextNode, contentKey, token);

                    if (valueResponse.HasValue)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Value response has value {valueResponse.Value}");
                        resultWasFound = true;
                        result = valueResponse.Value; // Shortcut so that once it find the value, it should stop.
                        await cts.CancelAsync();
                    }

                    if (_logger.IsDebug) _logger.Debug($"Value response has no value. Returning {valueResponse.Neighbours.Length} neighbours");
                    return valueResponse.Neighbours;
                },
                token
            );
        }
        catch (OperationCanceledException)
        {
            if (!resultWasFound) throw;
        }

        return result;
    }
}
