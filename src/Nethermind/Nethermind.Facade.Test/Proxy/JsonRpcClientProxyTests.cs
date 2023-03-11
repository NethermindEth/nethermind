// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
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
            _urlProxies = new[] { "http://localhost:8545" };
            _proxy = new JsonRpcClientProxy(_client, _urlProxies, LimboLogs.Instance);
        }

        [Test]
        public void constructor_should_throw_exception_if_client_argument_is_null()
        {
            Action act = () => _proxy = new JsonRpcClientProxy(null, _urlProxies, LimboLogs.Instance);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void constructor_should_throw_exception_if_url_proxy_is_not_valid_uri()
        {
            Action act = () => _proxy = new JsonRpcClientProxy(_client, new[] { "http:/localhost" }, LimboLogs.Instance);
            act.Should().Throw<UriFormatException>();
        }

        [Test]
        public void set_url_should_succeed_when_url_is_empty()
        {
            _proxy = new JsonRpcClientProxy(_client, null, LimboLogs.Instance);
            _proxy.SetUrls(null);
        }

        [Test]
        public void set_url_throw_exception_if_url_proxy_is_not_valid_uri()
        {
            _proxy = new JsonRpcClientProxy(_client, null, LimboLogs.Instance);
            Action act = () => _proxy.SetUrls("http:/localhost");
            act.Should().Throw<UriFormatException>();
        }


        [Test]
        public async Task send_async_should_invoke_client_post_json_and_return_ok_rpc_result()
        {
            const string method = "test";
            var data = new object();
            var @params = new List<object> { "arg1", 1, new object() };
            _client.PostJsonAsync<RpcResult<object>>(_urlProxies[0], Arg.Any<object>())
                .Returns(RpcResult<object>.Ok(data));
            var result = await _proxy.SendAsync<object>(method, @params);
            result.IsValid.Should().BeTrue();
            result.Result.Should().Be(data);
            await _client.Received().PostJsonAsync<RpcResult<object>>(_urlProxies[0], Arg.Any<object>());
        }

        [Test]
        public async Task send_async_should_not_invoke_client_post_json_and_return_null_when_url_is_empty()
        {
            const string method = "test";
            _proxy = new JsonRpcClientProxy(_client, null, LimboLogs.Instance);
            var @params = new List<object> { "arg1", 1, new object() };
            var result = await _proxy.SendAsync<object>(method, @params);
            result.Should().BeNull();
            await _client.DidNotReceiveWithAnyArgs().PostJsonAsync<RpcResult<object>>(null);
        }
    }
}
