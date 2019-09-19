//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Facade.Proxy;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Proxy
{
    public class JsonRpcClientProxyTests
    {
        private IJsonRpcClientProxy _proxy;
        private IHttpClient _client;
        private string[] _urlProxies;
        
        [SetUp]
        public void Setup()
        {
            _client = Substitute.For<IHttpClient>();
            _urlProxies = new[] {"http://localhost:8545"};
            _proxy = new JsonRpcClientProxy(_client, _urlProxies);
        }

        [Test]
        public void constructor_should_throw_exception_if_client_argument_is_null()
        {
            Action act = () => _proxy = new JsonRpcClientProxy(null, _urlProxies);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void constructor_should_throw_exception_if_url_proxies_argument_is_null()
        {
            Action act = () => _proxy = new JsonRpcClientProxy(_client, null);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void constructor_should_throw_exception_if_url_proxies_argument_is_empty()
        {
            Action act = () => _proxy = new JsonRpcClientProxy(_client, Array.Empty<string>());
            act.Should().Throw<ArgumentException>();
        }
        
        [Test]
        public void constructor_should_throw_exception_if_url_proxy_is_empty()
        {
            Action act = () => _proxy = new JsonRpcClientProxy(_client, new []{string.Empty});
            act.Should().Throw<ArgumentException>();
        }
        
        [Test]
        public void constructor_should_throw_exception_if_url_proxy_is_not_valid_uri()
        {
            Action act = () => _proxy = new JsonRpcClientProxy(_client, new []{"http:/localhost"});
            act.Should().Throw<UriFormatException>();
        }
        
        [Test]
        public async Task send_async_should_invoke_client_post_json_and_return_ok_rpc_result()
        {
            const string method = "test";
            var data = new object();
            var @params = new List<object> {"arg1", 1, new object()};
            _client.PostJsonAsync<RpcResult<object>>(_urlProxies[0], Arg.Any<object>())
                .Returns(RpcResult<object>.Ok(data));
            var result = await _proxy.SendAsync<object>(method, @params);
            result.IsValid.Should().BeTrue();
            result.Result.Should().Be(data);
        }
    }
}