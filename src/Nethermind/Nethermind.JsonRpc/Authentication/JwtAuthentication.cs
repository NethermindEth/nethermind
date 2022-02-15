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

using JWT;
using JWT.Algorithms;
using JWT.Serializers; // ToDo Nikita I think we should use Micsoroft library

namespace Nethermind.JsonRpc.Authentication
{
    public class JwtAuthentication : IRpcAuthentication
    {
        private readonly IJsonRpcConfig _jsonRpcConfig;
        public string? Secret { private get; set; } // ToDo secret shouldn't be byte array?
        private readonly IJwtDecoder _decoder;
        
        // ToDo Nikita: please add test cases that I sent you 
        public JwtAuthentication(
            IJsonRpcConfig jsonRpcConfig)
        {
            _jsonRpcConfig = jsonRpcConfig;
            IJsonSerializer serializer = new JsonNetSerializer(); // ToDo Niktia we should use our implementation of serializer
            IDateTimeProvider provider = new UtcDateTimeProvider();
            IJwtValidator validator = new JwtValidator(serializer, provider);
            IBase64UrlEncoder urlEncoder = new JwtBase64UrlEncoder();
            IJwtAlgorithm algorithm = new HMACSHA256Algorithm(); // ToDo Nikita what about IAT claims?
            _decoder = new JwtDecoder(serializer, validator, urlEncoder, algorithm);
            
            LoadSecret();
        }

        public string? AuthenticateAndDecode(string token)
        {
            if (Secret == null) return null;
            try
            {
                return _decoder.Decode(token, Secret, verify: true);;
            }
            catch
            {
                return null;
            }
        }

        private void LoadSecret()
        {
            Secret = _jsonRpcConfig.Secret;
            
            // ToDo Nikita
            // try to read from file first (add pathToFile to jsonRpcConfig)
            // from config
            // generate and store if not found:
            // I suggest move the authenticate attribute to RpcModule (not method) and generate JWT secret if we found at least one RPC module with authentication
        }
    }


}
