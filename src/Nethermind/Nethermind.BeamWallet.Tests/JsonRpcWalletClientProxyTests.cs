using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.BeamWallet.Clients;
using Nethermind.Core;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.BeamWallet.Tests
{
    public class JsonRpcWalletClientProxyTests
    {
        private IJsonRpcWalletClientProxy _jsonRpcWalletClientProxy;
        
        [SetUp]
        public void Setup()
        {
            var urls = new[] {"http://localhost:8545/"};
            var jsonRpcClientProxy=  new JsonRpcClientProxy(new DefaultHttpClient(new HttpClient(),
                new EthereumJsonSerializer(), LimboLogs.Instance), urls, LimboLogs.Instance);
            _jsonRpcWalletClientProxy = new JsonRpcWalletClientProxy(jsonRpcClientProxy);
        }

        [Test]
        public async Task personal_unlockAccount_should_succeed()
        {
            var address = new Address("0x0Bf4e8908A9D0f008FD4F4D216e3F5039CB0aD0E");
            var passphrase = "";
            var result = await _jsonRpcWalletClientProxy.personal_unlockAccount(address, passphrase);
            result.Should().NotBeNull();
            result.IsValid.Should().BeTrue();
            result.Result.Should().BeTrue();
        }
    }
}
