//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;
using Build = Nethermind.Runner.Test.Ethereum.Build;

namespace Nethermind.Merge.Plugin.Test;

public class JwtTest
{
    [Test]
    public async Task valid_token()
    {
        JwtProcessor processor = new("123");
        string token =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
            "eyJqc29ucnBjIjoiMi4wIiwibWV0aG9kIjoiZW5naW5lX2dldFBheWxvYWRWMSIsInBhcmFtcyI6WyIweGEyNDcyNDM3NTJlYjEwYjQiXSwiaWQiOjY3fQ." +
            "zdrxSPA1ZoeGb5_FXkd_rh62qeIMeb5i-HEliwhu3uw";
        JsonRpcRequest actual = processor.AuthenticateAndDecode(token)!;
        Assert.AreEqual("ID 67, engine_getPayloadV1(0xa247243752eb10b4)", actual.ToString());
    }
    
    [Test]
    public async Task wrong_secret()
    {
        JwtProcessor processor = new("12");
        string token =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
            "eyJmaWVsZDEiOiJkYXRhMSIsImFycmF5IjpbImVsZW0xIiwiZWxlbTIiLG51bGxdfQ." +
            "jzcbA6dAXbOU__NT7rrBwyGcBzTunxTKmQXzN4yU-2Y";
        JsonRpcRequest? actual = processor.AuthenticateAndDecode(token);
        Assert.AreEqual(null, actual);
    }
    
    [Test]
    public async Task wrong_algorithm_in_token_header()
    {
        JwtProcessor processor = new("123");
        string token =
            "eyJhbGciOiJIUzM4NCIsInR5cCI6IkpXVCJ9." +
            "eyJmaWVsZDEiOiJkYXRhMSIsImFycmF5IjpbImVsZW0xIiwiZWxlbTIiLG51bGxdfQ." +
            "H6n9LMKu8VJ06n4pxMK-Kes2nXl8L_2AjJT-VVBwDhxcRHer7UU5hlXAUPawxVYe";
        JsonRpcRequest? actual = processor.AuthenticateAndDecode(token);
        Assert.AreEqual(null, actual);
    }
    
    [Test]
    public async Task empty_json()
    {
        JwtProcessor processor = new("1234");
        string token =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkoyV1QifQ." +
            "e30." +
            "02YnbaptBoWN-QbWUkp4aCdsNvwUk2__NqrRWzh97To";
        JsonRpcRequest actual = processor.AuthenticateAndDecode(token)!;
        Assert.AreEqual("ID , ()", actual.ToString());
    }

    [Test]
    [TestCase("eyJhbGciOiJIUzI1NiIsInR5cCI6IkoyV1QifQ.02YnbaptBoWN-QbWUkp4aCdsNvwUk2__NqrRWzh97To")]
    [TestCase("")]
    public async Task incorrect_token_structure(string token)
    {
        JwtProcessor processor = new("1234");
        JsonRpcRequest? actual = processor.AuthenticateAndDecode(token);
        Assert.AreEqual(null, actual);
    }

    [Test]
    [TestCase(true)]
    [TestCase(false)]
    public async Task method_have_to_be_authenticated(bool authenticated)
    {
        var api = Build.ContextWithMocks();
        RpcModuleProvider rpcProvider = new(api.FileSystem, new JsonRpcConfig(), api.LogManager);
        TestRpcModule testModule = new ();
        rpcProvider.Register(new SingletonModulePool<ITestRpcModule>(testModule));
        JsonRpcUrl url = new("http", "localhost", 8550, RpcEndpoint.Http, new string[] { });
        JsonRpcContext context = new (RpcEndpoint.Http, url: url, authenticated: authenticated);
        ModuleResolution expected = authenticated ? ModuleResolution.Disabled : ModuleResolution.NotAuthenticated;
        Assert.AreEqual(expected, rpcProvider.Check("method_authenticated", context));
    }

    [Test]
    [TestCase(true)]
    [TestCase(false)]
    public async Task method_have_not_to_be_authenticated(bool authenticated)
    {
        var api = Build.ContextWithMocks();
        RpcModuleProvider rpcProvider = new(api.FileSystem, new JsonRpcConfig(), api.LogManager);
        TestRpcModule testModule = new ();
        rpcProvider.Register(new SingletonModulePool<ITestRpcModule>(testModule));
        JsonRpcUrl url = new("http", "localhost", 8550, RpcEndpoint.Http, new string[] { });
        JsonRpcContext context = new (RpcEndpoint.Http, url: url, authenticated: authenticated);
        ModuleResolution expected = ModuleResolution.Disabled;
        Assert.AreEqual(expected, rpcProvider.Check("method_notAuthenticated", context));
    }

    [RpcModule("Test")]
    private interface ITestRpcModule : IRpcModule
    {
        [JsonRpcMethod(
            IsSharable = true,
            IsImplemented = true,
            ShouldBeAuthenticated = true)]
        ResultWrapper<ExecutionStatusResult> method_authenticated();

        [JsonRpcMethod(
            IsSharable = true,
            IsImplemented = true,
            ShouldBeAuthenticated = false)]
        ResultWrapper<ExecutionStatusResult> method_notAuthenticated();
    }

    private class TestRpcModule : ITestRpcModule
    {
        public ResultWrapper<ExecutionStatusResult> method_authenticated()
        {
            return ResultWrapper<ExecutionStatusResult>.Fail("");
        }
        public ResultWrapper<ExecutionStatusResult> method_notAuthenticated()
        {
            return ResultWrapper<ExecutionStatusResult>.Fail("");
        }
    }
}
