using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.PubSub
{
    public class EmptySubscription : ISubscription
    {
        public Task PublishBlockAsync(Block block) => Task.CompletedTask;
        public Task PublishTransactionAsync(FullTransaction transaction) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}