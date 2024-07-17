// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia;

public class KBucket<THash, TValue>(int k, IMessageSender<THash, TValue> transport) where THash : notnull
{
    private DoubleEndedLru<THash> _items = new(k);
    private DoubleEndedLru<THash> _replacement = new(k); // Well, the replacement does not have to be k. Could be much lower.

    private Task? _refreshLastTask = null;
    public int Count => _items.Count;

    public void AddOrRefresh(THash item)
    {
        if (_items.AddOrRefresh(item))
        {
            return;
        }

        _replacement.AddOrRefresh(item);
        RefreshLast();
    }

    // TODO: Should have some sort of central thing for this?
    private void RefreshLast()
    {
        if (_refreshLastTask != null) return; // Last is already being refreshed

        if (!_items.TryGetLast(out THash? last)) return;

        _refreshLastTask = Task.Run(async () =>
        {

            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try
            {
                await transport.Ping(last!, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout
                _items.Remove(last!);

                // Add something from replacement
                if (_replacement.TryPopHead(out THash? replacement))
                {
                    AddOrRefresh(replacement!);
                }
            }
            catch (Exception)
            {
                // TODO: Log here
                throw;
            }
            finally
            {
                _refreshLastTask = null;
            }
        });
    }

    public THash[] GetAll()
    {
        // TODO: Seems like a good candidate to cache
        return _items.GetAll();
    }

    public void Remove(THash node)
    {
        _items.Remove(node);
    }
}
