// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

using Nethermind.Core.Collections;

namespace Nethermind.Core.PubSub
{
    public class CompositePublisher(params IPublisher[] publishers) : IPublisher
    {
        public async Task PublishAsync<T>(T data) where T : class
        {
            using ArrayPoolList<Task> tasks = new(publishers.Length);
            for (int i = 0; i < publishers.Length; i++)
            {
                tasks.Add(publishers[i].PublishAsync(data));
            }

            await Task.WhenAll(tasks.AsSpan());
        }

        public void Dispose()
        {
            foreach (IPublisher publisher in publishers)
            {
                publisher.Dispose();
            }
        }
    }
}
