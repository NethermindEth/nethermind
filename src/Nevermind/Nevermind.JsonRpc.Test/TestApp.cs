using System;

namespace Nevermind.JsonRpc.Test
{
    public class TestApp
    {
        public static void Main(string[] args)
        {
            var tests = new JsonRpcServiceTests();
            tests.Initialize();
            tests.NetVersionTest();
            tests.NetPeerCountTest();
            tests.Web3ShaTest();
            tests.GetBlockByNumberTest();
            tests.GetWorkTest();
            tests.IncorrectMethodNameTest();
            Console.WriteLine("Executed successfully");
            Console.ReadKey();
        }

    }
}