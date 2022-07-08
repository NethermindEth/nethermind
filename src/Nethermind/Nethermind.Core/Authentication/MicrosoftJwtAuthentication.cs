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
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Logging;

namespace Nethermind.Core.Authentication;

public class MicrosoftJwtAuthentication : IRpcAuthentication
{
    private readonly SecurityKey _securityKey;
    private readonly ILogger _logger;
    private readonly ITimestamper _timestamper;
    private const string JwtMessagePrefix = "Bearer ";
    private const int JwtTokenTtl = 5;
    private const int JwtSecretLength = 64;

    public MicrosoftJwtAuthentication(byte[] secret, ITimestamper timestamper, ILogger logger)
    {
        _securityKey = new SymmetricSecurityKey(secret);
        _logger = logger;
        _timestamper = timestamper;
    }

    public static MicrosoftJwtAuthentication CreateFromHexSecret(string hexSecret, ITimestamper timestamper, ILogger logger)
    {
        byte[] decodedSecret = DecodeSecret(hexSecret);
        return new MicrosoftJwtAuthentication(decodedSecret, timestamper, logger);
    }

    public static MicrosoftJwtAuthentication CreateFromFileOrGenerate(string filePath, ITimestamper timestamper, ILogger logger)
    {
        FileInfo fileInfo = new(filePath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            // Generate secret;
            if (logger.IsInfo) logger.Info("Generating authentication secret.");
            byte[] secret = new byte[JwtSecretLength / 2];
            Random rnd = new();
            rnd.NextBytes(secret);
            try
            {
                Directory.CreateDirectory(fileInfo.DirectoryName!);
                StreamWriter writer = new(filePath);
                string hexSecret = EncodeSecret(secret);
                writer.Write(hexSecret);
                writer.Close();
            }
            catch (IOException e)
            {
                if (logger.IsError)
                {
                    logger.Error($"Can't write authentication secret to file '{fileInfo.FullName}': {e.Message}");
                    logger.Error( "To change file location set property 'JsonRpc.JwtSecretFile'");
                }
                throw;
            }
            catch (UnauthorizedAccessException e)
            {
                if (logger.IsError)
                {
                    logger.Error($"Can't write authentication secret to file '{fileInfo.FullName}': access denied");
                    logger.Error( "To change file location set property 'JsonRpc.JwtSecretFile'");
                }
                throw;
            }

            if (logger.IsInfo) logger.Info($"Authentication secret has been written to file '{fileInfo.FullName}'.");
            return new MicrosoftJwtAuthentication(secret, timestamper, logger);
            
        }
        else
        {
            // Secret exists read from file
            if (logger.IsInfo) logger.Info($"Reading authentication secret from file '{fileInfo.FullName}'");
            string hexSecret;
            try
            {
                StreamReader stream = new(filePath);
                hexSecret = stream.ReadToEnd();
                stream.Close();
            }
            catch (IOException e)
            {
                if (logger.IsError)
                {
                    logger.Error($"Can't read authentication secret from file '{fileInfo.FullName}': {e.Message}");
                    logger.Error( "To change file location set property 'JsonRpc.JwtSecretFile'");
                }
                throw;
            }
            catch (UnauthorizedAccessException e)
            {
                if (logger.IsError)
                {
                    logger.Error($"Can't read authentication secret from file '{fileInfo.FullName}': access denied");
                    logger.Error( "To change file location set property 'JsonRpc.JwtSecretFile'");
                }
                throw;
            }

            hexSecret = hexSecret.TrimStart().TrimEnd();
            if (!System.Text.RegularExpressions.Regex.IsMatch(hexSecret, @"^(0x)?[0-9a-fA-F]{64}$"))
            {
                if (logger.IsError)
                {
                    logger.Error("Specified authentication secret is not a 64 digit hexadecimal number.");
                    logger.Error( $"Delete file '{fileInfo.FullName}' to generate new secret or set property 'JsonRpc.JwtSecretFile'");
                }

                throw new FormatException("JWT secret should be a 64 digit hexadecimal number");
            }
            return CreateFromHexSecret(hexSecret, timestamper, logger);
        }
    }

    private static string EncodeSecret(byte[] secret)
    {
        return BitConverter.ToString(secret).Replace("-", "");
    }

    public bool Authenticate(string? token)
    {
        if (token == null)
        {
            if (_logger.IsWarn) _logger.Warn("Message authentication error: Can't find authentication token"); 
            return false;
        }

        if (!token.StartsWith(JwtMessagePrefix))
        {
            if (_logger.IsWarn) _logger.Warn("Message authentication error: Authentication token should start with 'Bearer '");
            return false;
        }
        token = token.Remove(0, JwtMessagePrefix.Length);
        TokenValidationParameters tokenValidationParameters = new TokenValidationParameters
        {
            IssuerSigningKey = _securityKey,
            RequireExpirationTime = false,
            ValidateLifetime = true,
            ValidateAudience = false,
            ValidateIssuer = false,
            LifetimeValidator = LifetimeValidator
        };

        try
        {
            JwtSecurityTokenHandler handler = new();
            SecurityToken securityToken;
            handler.ValidateToken(token, tokenValidationParameters, out securityToken);
            JwtSecurityToken jwtToken = handler.ReadJwtToken(token);
            long iat = ((DateTimeOffset)jwtToken.IssuedAt).ToUnixTimeSeconds();
            DateTime now = _timestamper.UtcNow;
            if (Math.Abs(iat - ((DateTimeOffset)now).ToUnixTimeSeconds()) <= JwtTokenTtl)
            {
                if (_logger.IsTrace)
                    _logger.Trace($"Message authenticated. Token: {token}, iat: {jwtToken.IssuedAt}, time: {now}");
                return true;
            }

            if (_logger.IsWarn)
                _logger.Warn($"Authentication token is outdated. Now is {now}, token issued at {jwtToken.IssuedAt}");
            return false;
        }
        catch (SecurityTokenDecryptionFailedException)
        {
            if (_logger.IsWarn) _logger.Warn("Message authentication error: Can't decrypt provided security token");
            return false;
        }
        catch (SecurityTokenReplayDetectedException)
        {
            if (_logger.IsWarn) _logger.Warn("Message authentication error: Token has been used multiple times");
            return false;
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            if (_logger.IsWarn) _logger.Warn("Message authentication error: Invalid signature");
            return false;
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Message authentication error: {e.Message}");
            return false;
        }
    }

    private bool LifetimeValidator(
        DateTime? notBefore,
        DateTime? expires,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters)
    {
        if (expires == null) return true;
        long exp = ((DateTimeOffset)expires).ToUnixTimeSeconds();
        return _timestamper.UnixTime.SecondsLong < exp;
    }

    private static byte[] DecodeSecret(string hexSecret)
    {
        int start = hexSecret.StartsWith("0x") ? 2 : 0;
        return Enumerable.Range(start, hexSecret.Length - start)
            .Where(x => x % 2 == 0)
            .Select(x => Convert.ToByte(hexSecret.Substring(x, 2), 16))
            .ToArray();
    }
}
