using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using FluentAssertions;

namespace Nethermind.IntegrationTests;

public static class Utils
{
    public static async Task<string> GetCleanStdoutAsync(this IContainer container)
    {
        var (stdout, _) = await container.GetLogsAsync();
        
        // Strip ANSI escape codes that Nethermind uses for colored output
        return Regex.Replace(stdout, @"\e\[[0-9;]*m", string.Empty);
    }

    public static async Task<string> GetCleanStderrAsync(this IContainer container)
    {
        var (_, stderr) = await container.GetLogsAsync();
        
        // Strip ANSI escape codes that Nethermind uses for colored output
        return Regex.Replace(stderr, @"\e\[[0-9;]*m", string.Empty);
    }

    public static string CreateJwtToken(string jwtSecretHex)
    {
        var secretBytes = Convert.FromHexString(jwtSecretHex.StartsWith("0x") ? jwtSecretHex.Substring(2) : jwtSecretHex);
        
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"iat\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        
        string signature;
        using (var hmac = new System.Security.Cryptography.HMACSHA256(secretBytes))
        {
            var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{header}.{payload}"));
            signature = Convert.ToBase64String(signatureBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        
        return $"{header}.{payload}.{signature}";
    }

    public static async Task<JsonNode> SendEngineRequestAsync(HttpClient httpClient, string method, params object[] parameters)
    {
        var request = new
        {
            jsonrpc = "2.0",
            method = method,
            @params = parameters,
            id = 1
        };
        
        var jsonString = JsonSerializer.Serialize(request);
        var content = new StringContent(jsonString, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("", content);
        response.EnsureSuccessStatusCode();
        var responseBody = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(responseBody);
        
        if (json["error"] != null)
        {
            throw new Exception($"Engine API error on {method}: {json["error"]}\nRequest: {jsonString}");
        }
        
        return json["result"];
    }

    public static async Task CreateBlocksAsync(HttpClient httpClient, int count, int version, long minimumTimestamp)
    {
        for (int i = 0; i < count; i++)
        {
            var latestBlock = await SendEngineRequestAsync(httpClient, "eth_getBlockByNumber", "latest", false);
            if (latestBlock == null) throw new Exception("Latest block is null.");
            var parentHash = latestBlock["hash"].GetValue<string>();
            var parentTimestampStr = latestBlock["timestamp"].GetValue<string>();
            long parentTimestamp = Convert.ToInt64(parentTimestampStr.Substring(2), 16);
            
            long newTimestamp = Math.Max(minimumTimestamp + (i * 12), parentTimestamp + 12);
            var timestampHex = $"0x{newTimestamp:x}";

            var forkchoiceState = new
            {
                headBlockHash = parentHash,
                safeBlockHash = parentHash,
                finalizedBlockHash = parentHash
            };

            object payloadAttributes;
            if (version == 1)
            {
                payloadAttributes = new
                {
                    timestamp = timestampHex,
                    prevRandao = "0x0000000000000000000000000000000000000000000000000000000000000000",
                    suggestedFeeRecipient = "0x0000000000000000000000000000000000000000"
                };
            }
            else if (version == 2)
            {
                payloadAttributes = new
                {
                    timestamp = timestampHex,
                    prevRandao = "0x0000000000000000000000000000000000000000000000000000000000000000",
                    suggestedFeeRecipient = "0x0000000000000000000000000000000000000000",
                    withdrawals = new object[] { }
                };
            }
            else if (version == 3)
            {
                payloadAttributes = new
                {
                    timestamp = timestampHex,
                    prevRandao = "0x0000000000000000000000000000000000000000000000000000000000000000",
                    suggestedFeeRecipient = "0x0000000000000000000000000000000000000000",
                    withdrawals = new object[] { },
                    parentBeaconBlockRoot = "0x0000000000000000000000000000000000000000000000000000000000000000"
                };
            }
            else if (version == 4)
            {
                payloadAttributes = new
                {
                    timestamp = timestampHex,
                    prevRandao = "0x0000000000000000000000000000000000000000000000000000000000000000",
                    suggestedFeeRecipient = "0x0000000000000000000000000000000000000000",
                    withdrawals = new object[] { },
                    parentBeaconBlockRoot = "0x0000000000000000000000000000000000000000000000000000000000000000"
                };
            }
            else
            {
                throw new NotSupportedException($"Version {version} not supported.");
            }

            var fcuResult1 = await SendEngineRequestAsync(httpClient, $"engine_forkchoiceUpdatedV{version}", forkchoiceState, payloadAttributes);
            var payloadStatus = fcuResult1["payloadStatus"]["status"].GetValue<string>();
            if (payloadStatus != "VALID")
            {
                throw new Exception($"FCU1 Failed. Status: {payloadStatus}. Result: {fcuResult1.ToJsonString()}");
            }
            
            var payloadId = fcuResult1["payloadId"]?.GetValue<string>();
            payloadId.Should().NotBeNullOrEmpty();

            var getPayloadResult = await SendEngineRequestAsync(httpClient, $"engine_getPayloadV{version}", payloadId);
            JsonNode executionPayload = version == 1 ? getPayloadResult : getPayloadResult["executionPayload"];
            
            object[] newPayloadParams;
            if (version == 1) 
            {
                newPayloadParams = new object[] { executionPayload };
            }
            else if (version == 2)
            {
                newPayloadParams = new object[] { executionPayload };
            }
            else if (version == 3)
            {
                newPayloadParams = new object[] { executionPayload, new object[] { }, "0x0000000000000000000000000000000000000000000000000000000000000000" };
            }
            else
            {
                newPayloadParams = new object[] { executionPayload, new object[] { }, "0x0000000000000000000000000000000000000000000000000000000000000000", new object[] { } };
            }

            var newPayloadResult = await SendEngineRequestAsync(httpClient, $"engine_newPayloadV{version}", newPayloadParams);
            var newPayloadStatus = newPayloadResult["status"].GetValue<string>();
            newPayloadStatus.Should().BeOneOf("VALID", "SYNCING", "ACCEPTED");
            
            var newBlockHash = executionPayload["blockHash"].GetValue<string>();
            var finalForkchoiceState = new
            {
                headBlockHash = newBlockHash,
                safeBlockHash = newBlockHash,
                finalizedBlockHash = newBlockHash
            };
            var fcuResult2 = await SendEngineRequestAsync(httpClient, $"engine_forkchoiceUpdatedV{version}", finalForkchoiceState);
            fcuResult2["payloadStatus"]["status"].GetValue<string>().Should().BeOneOf("VALID", "SYNCING", "ACCEPTED");
        }
    }
}
