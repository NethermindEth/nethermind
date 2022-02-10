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
using JWT.Serializers;
using IJsonSerializer = JWT.IJsonSerializer;

namespace Nethermind.JsonRpc
{
    public class JwtProcessor
    {
        public string? Secret { private get; set; }
        private IJsonSerializer _serializer;
        private IDateTimeProvider _provider;
        private IJwtValidator _validator;
        private IBase64UrlEncoder _urlEncoder;
        private IJwtAlgorithm _algorithm;
        private IJwtDecoder _decoder;
        private static JwtProcessor? _instance;

        public static JwtProcessor Instance => _instance ??= new JwtProcessor();

        private JwtProcessor()
        {
            Secret = null;
            _serializer = new JsonNetSerializer();
            _provider = new UtcDateTimeProvider();
            _validator = new JwtValidator(_serializer, _provider);
            _urlEncoder = new JwtBase64UrlEncoder();
            _algorithm = new HMACSHA256Algorithm();
            _decoder = new JwtDecoder(_serializer, _validator, _urlEncoder, _algorithm);
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
    }
}
