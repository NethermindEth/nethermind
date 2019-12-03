using System;
using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Json;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.EndToEnd.Test
{
    class Program
    {
        public static async Task Main(string[] args)
        {
            var logsManager = LimboLogs.Instance;
            var client = new JsonRpcClientProxy(new DefaultHttpClient(new HttpClient(), new EthereumJsonSerializer(),
                logsManager, 0), new[] {"http://localhost:8545"}, logsManager);

            Console.WriteLine("Press any key to start the tests.");
            Console.ReadKey();

            Console.WriteLine("* Creating an account *");
            var password = "test";
            var accountResult = await client.SendAsync<Address>("personal_newAccount", password);
            var account = accountResult.Result;
            Console.WriteLine($"* Created an account: {account} *");

            Console.WriteLine($"* Unlocking an account: {account} *");
            var unlockedResult = await client.SendAsync<bool>("personal_unlockAccount", account, password);
            Console.WriteLine($"* Unlocked an account: {account}, {unlockedResult.Result} *");

            Console.WriteLine($"* Changing NDM account: {account} *");
            var changeAddress = await client.SendAsync<Address>("ndm_changeConsumerAddress", account);
            Console.WriteLine($"* Changed NDM account: {account}, {changeAddress.IsValid} *");

            Console.WriteLine("Press any key to quit.");
            Console.ReadKey();
        }
    }
}