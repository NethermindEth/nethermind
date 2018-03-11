using System.Threading.Tasks;

namespace Nethermind.Runner.TestClient
{
    public interface IRunnerTestCient
    {
        Task<string> SendEthProtocolVersion();
        Task<string> SendEthGetBlockNumber(string blockNumber, bool returnFullTransactionObjects);
        string SendNetVersion();
        string SendWeb3ClientVersion();
        string SendWeb3Sha3(string content);
    }
}