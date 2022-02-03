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

using System;
using JWT;
using JWT.Algorithms;
using JWT.Exceptions;
using JWT.Serializers;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;
using IJsonSerializer = JWT.IJsonSerializer;

namespace Nethermind.Merge.Plugin;

public class JwtProcessor
{
    private string _secret;
    
    public JwtProcessor(string secret)
    {
        _secret = secret;
    }
    
    public JsonRpcRequest? AuthenticateAndDecode(string token)
    {
        try
        {
            IJsonSerializer serializer = new EthereumJsonSerializerWrapper();
            IDateTimeProvider provider = new UtcDateTimeProvider();
            IJwtValidator validator = new JwtValidator(serializer, provider);
            IBase64UrlEncoder urlEncoder = new JwtBase64UrlEncoder();
            IJwtAlgorithm algorithm = new HMACSHA256Algorithm();
            IJwtDecoder decoder = new JwtDecoder(serializer, validator, urlEncoder, algorithm);
    
            JsonRpcRequest? json = decoder.DecodeToObject<JsonRpcRequest>(token, _secret, verify: true);
            Console.WriteLine(json);
            return json;
        }
        catch
        {
            return null;
        }

    }
    
    private class EthereumJsonSerializerWrapper : IJsonSerializer
    {
        private readonly EthereumJsonSerializer _ethereumJsonSerializer = new();
        
        public string Serialize(object obj)
        {
            return _ethereumJsonSerializer.Serialize(obj);
        }

        public T Deserialize<T>(string json)
        {
            return _ethereumJsonSerializer.Deserialize<T>(json);
        }
    }
}
