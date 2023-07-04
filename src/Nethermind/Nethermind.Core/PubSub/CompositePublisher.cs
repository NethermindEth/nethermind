// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.Core.PubSub
{
    public class CompositePublisher : IPublisher
    {
        private readonly IPublisher[] _publishers;

        public CompositePublisher(params IPublisher[] publishers)
        {
            _publishers = publishers;
        }

        public async Task PublishAsync<T>(T data) where T : class
        {
            Task[] tasks = new Task[_publishers.Length];
            for (int i = 0; i < _publishers.Length; i++)
            {
                tasks[i] = _publishers[i].PublishAsync(data);
            }

            await Task.WhenAll(tasks);
        }

        public void Dispose()
        {
            foreach (IPublisher publisher in _publishers)
            {
                publisher.Dispose();
            }
        }
    }
}
