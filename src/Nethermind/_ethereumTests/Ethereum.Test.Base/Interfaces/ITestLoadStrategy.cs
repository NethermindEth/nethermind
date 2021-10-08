using System.Collections.Generic;

namespace Ethereum.Test.Base.Interfaces
{
    public interface ITestLoadStrategy
    {
        IEnumerable<IEthereumTest> Load(string testDirectoryName, string wildcard = null);
    }
}