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
using System.IO;
using System.Linq;
using JWT;
using JWT.Algorithms;
using JWT.Serializers;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Authentication;
using Nethermind.Logging;

namespace Nethermind.Core.Authentication
{
    public class JwtAuthentication : IRpcAuthentication
    {
        private IClock _clock;
        public byte[]? Secret { private get; set; }
        private readonly IJwtDecoder _decoder;

        private const string JwtMessagePrefix = "Bearer ";
        private const int JwtTokenTtl = 5;
        private const int JwtSecretLength = 64;

        public JwtAuthentication(
            byte[] secret,
            IClock clock)
        {
            _clock = clock;
            IJsonSerializer serializer = new JsonNetSerializer();
            IDateTimeProvider provider = new DateTimeProviderWrapper(_clock);
            IJwtValidator validator = new JwtValidator(serializer, provider, 0);
            IBase64UrlEncoder urlEncoder = new JwtBase64UrlEncoder();
            IJwtAlgorithm algorithm = new HMACSHA256Algorithm();
            _decoder = new JwtDecoder(serializer, validator, urlEncoder, algorithm);
            Secret = secret;
        }

        public static JwtAuthentication CreateFromHexSecret(string hexSecret, IClock clock)
        {
            byte[] decodedSecret = DecodeSecret(hexSecret);
            return new JwtAuthentication(decodedSecret, clock);
        }

        public static JwtAuthentication CreateFromFileOrGenerate(string filePath, IClock clock, ILogger logger)
        {
            FileInfo fileInfo = new(filePath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                // Generate secret;
                logger.Info("Generating jwt secret");
                byte[] secret = new byte[JwtSecretLength / 2];
                Random rnd = new();
                rnd.NextBytes(secret);
                Directory.CreateDirectory(fileInfo.DirectoryName!);
                StreamWriter writer = new(filePath);
                string hexSecret = EncodeSecret(secret);
                writer.Write(hexSecret);
                writer.Close();
                logger.Info($"Secret have been written to {fileInfo.FullName}");
                return new JwtAuthentication(secret, clock);
            }
            else
            {
                // Secret exists read from file
                logger.Info("Reading jwt secret from file");
                StreamReader stream = new(filePath);
                string hexSecret = stream.ReadToEnd();
                hexSecret = hexSecret.TrimStart().TrimEnd();
                if (!System.Text.RegularExpressions.Regex.IsMatch(hexSecret, @"^(0x)?[0-9a-fA-F]{64}$"))
                {
                    throw new FormatException("Secret should be a 64 digit hexadecimal number");
                }
                return CreateFromHexSecret(hexSecret, clock);
            }
        }

        public bool Authenticate(string? token)
        {
            if (token == null) return false;
            if (Secret == null) return false;
            if (!token.StartsWith(JwtMessagePrefix)) return false;
            token = token.Remove(0, JwtMessagePrefix.Length);
            try
            {
                var decoded = _decoder.DecodeToObject<Dictionary<string, object>>(token, Secret, true);
                long iat = (long)decoded["iat"];
                long cur = _clock.GetCurrentTime();
                return Math.Abs(iat - cur) <= JwtTokenTtl;
            }
            catch (Exception e)
            {
                return false;
            }
        }

        private static byte[] DecodeSecret(string hexSecret)
        {
            int start = hexSecret.StartsWith("0x") ? 2 : 0;
            return Enumerable.Range(start, hexSecret.Length - start)
                .Where(x => x % 2 == 0)
                .Select(x => Convert.ToByte(hexSecret.Substring(x, 2), 16))
                .ToArray();
        }

        private static string EncodeSecret(byte[] secret)
        {
            return BitConverter.ToString(secret).Replace("-", "");
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
