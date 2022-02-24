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
using System.Collections.Generic;
using System.Linq;
using JWT;
using JWT.Algorithms;
using JWT.Builder;
using JWT.Serializers; // ToDo Nikita I think we should use Micsoroft library

namespace Nethermind.JsonRpc.Authentication
{
    public class JwtAuthentication : IRpcAuthentication
    {
        private IClock _clock;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        public byte[]? Secret { private get; set; }
        private readonly IJwtDecoder _decoder;

        private const string JWT_MESSAGE_PREFIX = "Bearer ";
        
        public JwtAuthentication(
            IJsonRpcConfig jsonRpcConfig,
            IClock clock)
        {
            _clock = clock;
            _jsonRpcConfig = jsonRpcConfig;
            IJsonSerializer serializer = new JsonNetSerializer(); // ToDo Niktia we should use our implementation of serializer
            IDateTimeProvider provider = new DateTimeProviderWrapper(_clock);
            IJwtValidator validator = new JwtValidator(serializer, provider, 5);
            IBase64UrlEncoder urlEncoder = new JwtBase64UrlEncoder();
            IJwtAlgorithm algorithm = new HMACSHA256Algorithm(); // ToDo Nikita what about IAT claims?
            _decoder = new JwtDecoder(serializer, validator, urlEncoder, algorithm);
            LoadSecret();
        }

        public bool Authenticate(string token)
        {
            if (Secret == null) return false;
            if (!token.StartsWith(JWT_MESSAGE_PREFIX)) return false;
            token = token.Remove(0, JWT_MESSAGE_PREFIX.Length);
            try
            {
                var decoded = _decoder.DecodeToObject<Dictionary<string, object>>(token, Secret, true);
                long iat = (long)decoded["iat"];
                long cur = _clock.GetCurrentTime();
                return Math.Abs(iat - cur) <= 5;
            }
            catch (Exception e)
            {
                return false;
            }
        }

        private void LoadSecret()
        {
            string hex = _jsonRpcConfig.Secret;

            Secret = Enumerable.Range(0, hex.Length)
                .Where(x => x % 2 == 0)
                .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                .ToArray();

            // ToDo Nikita
            // try to read from file first (add pathToFile to jsonRpcConfig)
            // from config
            // generate and store if not found:
            // I suggest move the authenticate attribute to RpcModule (not method) and generate JWT secret if we found at least one RPC module with authentication
        }

        private class DateTimeProviderWrapper : IDateTimeProvider
        {
            private IClock _clock;
            public DateTimeProviderWrapper(IClock clock)
            {
                _clock = clock;
            }
            public DateTimeOffset GetNow()
            {
                return DateTimeOffset.FromUnixTimeSeconds(_clock.GetCurrentTime());
            }
        }
    }


}
