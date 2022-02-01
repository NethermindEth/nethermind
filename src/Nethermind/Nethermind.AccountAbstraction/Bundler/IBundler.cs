using Nethermind.Core;

namespace Nethermind.AccountAbstraction.Bundler
{
    public interface IBundler
    {
        public void Bundle(Block head);
    }
}
