using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ethereum.Test.Base.Interfaces
{
    public interface IBlockchainTestRunner
    {
        Task<IEnumerable<EthereumTestResult>> RunTestsAsync();
    }
}