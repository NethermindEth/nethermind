using System;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;

namespace Nethermind.Runner.TestClient
{
    public class RunnerTestCientApp : IRunnerTestCientApp
    {
        private readonly IRunnerTestCient _cient;

        public RunnerTestCientApp(IRunnerTestCient cient)
        {
            _cient = cient;
        }

        public void Start()
        {
            while (true)
            {
                Console.WriteLine("Options: 1 - eth_protocolVersion, 2 - eth_getBlockByNumber, e - exit");
                Console.WriteLine("Enter command: ");
                var action = Console.ReadLine();
                if (action.CompareIgnoreCaseTrim("e"))
                {
                    return;
                }
                else if (action.CompareIgnoreCaseTrim("1"))
                {
                    var result = Task.Run(() => _cient.SendEthProtocolVersion());
                    result.Wait();
                    PrintResult(result.Result);
                }
                else if (action.CompareIgnoreCaseTrim("2"))
                {
                    var result = Task.Run(() => _cient.SendEthGetBlockNumber("0x0", false));
                    result.Wait();
                    PrintResult(result.Result);
                }
                else
                {
                    Console.WriteLine("Incorrect command");
                }
            }
        }

        private void PrintResult(string result)
        {
            Console.WriteLine("Response:");
            Console.WriteLine(result);
        }
    }
}