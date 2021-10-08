using System.Collections.Generic;

namespace Ethereum.Test.Base.Interfaces
{
    public interface ITestSourceLoader
    {
        IEnumerable<IEthereumTest> LoadTests();
    }
}