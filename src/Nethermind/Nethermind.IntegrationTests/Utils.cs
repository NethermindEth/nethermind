using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.IntegrationTests;

public static class Utils
{
    private const string DefaultLocalImageTag = "nethermind:integration-tests";

    private static readonly SemaphoreSlim s_imageBuildLock = new(1, 1);
    private static IFutureDockerImage s_builtImage;

    /// <summary>
    /// Resolves the Nethermind container image to use for integration tests.
    /// If <c>NETHERMIND_IMAGE</c> is set, it takes precedence and is used as-is.
    /// Otherwise the repository's <c>Dockerfile</c> is built (once per test process)
    /// and tagged with <c>NETHERMIND_IMAGE_TAG</c> (default <c>nethermind:integration-tests</c>).
    /// </summary>
    public static async Task<string> GetNethermindImageAsync()
    {
        string overrideImage = Environment.GetEnvironmentVariable("NETHERMIND_IMAGE");
        if (!string.IsNullOrWhiteSpace(overrideImage))
        {
            return overrideImage;
        }

        string tag = Environment.GetEnvironmentVariable("NETHERMIND_IMAGE_TAG") ?? DefaultLocalImageTag;

        await s_imageBuildLock.WaitAsync();
        try
        {
            if (s_builtImage is null)
            {
                IFutureDockerImage image = new ImageFromDockerfileBuilder()
                    .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), string.Empty)
                    .WithDockerfile("Dockerfile")
                    .WithName(tag)
                    .WithCleanUp(false)
                    .Build();
                await image.CreateAsync();
                s_builtImage = image;
            }
        }
        finally
        {
            s_imageBuildLock.Release();
        }

        return s_builtImage.FullName;
    }

    /// <summary>
    /// Builds a <see cref="ContainerBuilder"/> pre-configured for a Nethermind node:
    /// uses the resolved image, binds the JSON-RPC (8545) and Engine (8551) ports,
    /// and (optionally) waits until <c>Initialization Completed</c> appears in logs.
    /// </summary>
    public static async Task<ContainerBuilder> BuildNethermindContainerAsync(string[] command, bool waitForInit = true, (string HostPath, string ContainerPath)? bindMount = null, IReadOnlyList<(string HostPath, string ContainerPath)> bindMounts = null)
    {
        string image = await GetNethermindImageAsync();

        ContainerBuilder builder = new ContainerBuilder()
            .WithImage(image)
            .WithCommand(command)
            .WithPortBinding(8545, true)
            .WithPortBinding(8551, true);

        if (bindMount is not null)
        {
            builder = builder.WithBindMount(bindMount.Value.HostPath, bindMount.Value.ContainerPath);
        }

        if (bindMounts is not null)
        {
            foreach ((string HostPath, string ContainerPath) m in bindMounts)
            {
                builder = builder.WithBindMount(m.HostPath, m.ContainerPath);
            }
        }

        if (waitForInit)
        {
            builder = builder.WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Initialization Completed"));
        }

        return builder;
    }

    /// <summary>
    /// Extracts an embedded chainspec resource (under <c>Resources/</c>) to a temp file
    /// on the host so it can be bind-mounted into the container.
    /// </summary>
    public static string ExtractEmbeddedChainspec(string resourceFileName)
    {
        Assembly assembly = typeof(Utils).Assembly;
        string resourceName = $"{assembly.GetName().Name}.Resources.{resourceFileName}";
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");

        string tmpPath = Path.Combine(Path.GetTempPath(), $"nethermind-it-{Guid.NewGuid():N}-{resourceFileName}");
        using FileStream output = File.Create(tmpPath);
        stream.CopyTo(output);
        return tmpPath;
    }

    public static async Task<string> GetCleanStdoutAsync(this IContainer container)
    {
        (string stdout, string _) = await container.GetLogsAsync();

        // Strip ANSI escape codes that Nethermind uses for colored output
        return Regex.Replace(stdout, @"\e\[[0-9;]*m", string.Empty);
    }

    public static async Task<string> GetCleanStderrAsync(this IContainer container)
    {
        (string _, string stderr) = await container.GetLogsAsync();

        // Strip ANSI escape codes that Nethermind uses for colored output
        return Regex.Replace(stderr, @"\e\[[0-9;]*m", string.Empty);
    }

    public static string CreateJwtToken(string jwtSecretHex)
    {
        byte[] secretBytes = Convert.FromHexString(jwtSecretHex.StartsWith("0x") ? jwtSecretHex.Substring(2) : jwtSecretHex);

        string header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"iat\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        string signature;
        using (System.Security.Cryptography.HMACSHA256 hmac = new(secretBytes))
        {
            byte[] signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{header}.{payload}"));
            signature = Convert.ToBase64String(signatureBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        return $"{header}.{payload}.{signature}";
    }

    /// <summary>
    /// Signs a legacy (Type 0) transaction with EIP-155 replay protection and submits it
    /// via <c>eth_sendRawTransaction</c>. Returns the resulting tx hash.
    /// </summary>
    public static async Task<string> SignAndSendTransactionAsync(HttpClient httpClient, PrivateKey signer, Transaction tx, ulong chainId)
    {
        EthereumEcdsa ecdsa = new(chainId);
        ecdsa.Sign(signer, tx, isEip155Enabled: true);

        Rlp rlp = TxDecoder.Instance.Encode(tx);
        string raw = "0x" + rlp.Bytes.ToHexString();

        JsonNode result = await SendJsonRpcRequestAsync(httpClient, "eth_sendRawTransaction", raw);
        return result.GetValue<string>();
    }

    /// <summary>
    /// JSON-RPC POST against the public Eth endpoint (no JWT). Same shape as
    /// <see cref="SendEngineRequestAsync"/> but exists as a separate method so callers
    /// can hit a different port without auth.
    /// </summary>
    public static async Task<JsonNode> SendJsonRpcRequestAsync(HttpClient httpClient, string method, params object[] parameters)
    {
        var request = new
        {
            jsonrpc = "2.0",
            method = method,
            @params = parameters,
            id = 1
        };

        string jsonString = JsonSerializer.Serialize(request);
        StringContent content = new(jsonString, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await httpClient.PostAsync("", content);
        response.EnsureSuccessStatusCode();
        string responseBody = await response.Content.ReadAsStringAsync();
        JsonNode json = JsonNode.Parse(responseBody);

        if (json["error"] != null)
        {
            throw new Exception($"JSON-RPC error on {method}: {json["error"]}\nRequest: {jsonString}");
        }

        return json["result"];
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

        string jsonString = JsonSerializer.Serialize(request);
        StringContent content = new(jsonString, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await httpClient.PostAsync("", content);
        response.EnsureSuccessStatusCode();
        string responseBody = await response.Content.ReadAsStringAsync();
        JsonNode json = JsonNode.Parse(responseBody);

        if (json["error"] != null)
        {
            throw new Exception($"Engine API error on {method}: {json["error"]}\nRequest: {jsonString}");
        }

        return json["result"];
    }

    public static async Task CreateBlocksAsync(HttpClient httpClient, int count, int version, long minimumTimestamp)
    {
        // Amsterdam (EIP-7928 / EIP-7843) reuses FCU V4 but switches getPayload→V6 / newPayload→V5.
        // We model that as `version: 5` here so callers can opt in without leaking three handler
        // numbers everywhere.
        int fcuVersion = version == 5 ? 4 : version;
        int getPayloadVersion = version == 5 ? 6 : version;
        int newPayloadVersion = version == 5 ? 5 : version;

        for (int i = 0; i < count; i++)
        {
            JsonNode latestBlock = await SendEngineRequestAsync(httpClient, "eth_getBlockByNumber", "latest", false);
            if (latestBlock == null) throw new Exception("Latest block is null.");
            string parentHash = latestBlock["hash"].GetValue<string>();
            string parentTimestampStr = latestBlock["timestamp"].GetValue<string>();
            long parentTimestamp = Convert.ToInt64(parentTimestampStr.Substring(2), 16);

            long newTimestamp = Math.Max(minimumTimestamp + (i * 12), parentTimestamp + 12);
            string timestampHex = $"0x{newTimestamp:x}";

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
                    withdrawals = Array.Empty<object>()
                };
            }
            else if (version == 3)
            {
                payloadAttributes = new
                {
                    timestamp = timestampHex,
                    prevRandao = "0x0000000000000000000000000000000000000000000000000000000000000000",
                    suggestedFeeRecipient = "0x0000000000000000000000000000000000000000",
                    withdrawals = Array.Empty<object>(),
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
                    withdrawals = Array.Empty<object>(),
                    parentBeaconBlockRoot = "0x0000000000000000000000000000000000000000000000000000000000000000"
                };
            }
            else if (version == 5)
            {
                // EIP-7843 adds `slotNumber` to PayloadAttributes; FCU V4 requires it once Amsterdam is active.
                // The slot number must be strictly greater than the parent block's slot number, so we
                // anchor on the parent block number we just read (parent + 1) and add the per-call offset
                // — this keeps slot numbers monotonic across multiple CreateBlocksAsync invocations.
                long parentNumber = Convert.ToInt64(latestBlock["number"].GetValue<string>().Substring(2), 16);
                payloadAttributes = new
                {
                    timestamp = timestampHex,
                    prevRandao = "0x0000000000000000000000000000000000000000000000000000000000000000",
                    suggestedFeeRecipient = "0x0000000000000000000000000000000000000000",
                    withdrawals = Array.Empty<object>(),
                    parentBeaconBlockRoot = "0x0000000000000000000000000000000000000000000000000000000000000000",
                    slotNumber = $"0x{parentNumber + i + 1:x}"
                };
            }
            else
            {
                throw new NotSupportedException($"Version {version} not supported.");
            }

            JsonNode fcuResult1 = await SendEngineRequestAsync(httpClient, $"engine_forkchoiceUpdatedV{fcuVersion}", forkchoiceState, payloadAttributes);
            string payloadStatus = fcuResult1["payloadStatus"]["status"].GetValue<string>();
            if (payloadStatus != "VALID")
            {
                throw new Exception($"FCU1 Failed. Status: {payloadStatus}. Result: {fcuResult1.ToJsonString()}");
            }

            string payloadId = fcuResult1["payloadId"]?.GetValue<string>();
            payloadId.Should().NotBeNullOrEmpty();

            JsonNode getPayloadResult = await SendEngineRequestAsync(httpClient, $"engine_getPayloadV{getPayloadVersion}", payloadId);
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
                newPayloadParams = new object[] { executionPayload, Array.Empty<object>(), "0x0000000000000000000000000000000000000000000000000000000000000000" };
            }
            else if (version == 4)
            {
                newPayloadParams = new object[] { executionPayload, Array.Empty<object>(), "0x0000000000000000000000000000000000000000000000000000000000000000", Array.Empty<object>() };
            }
            else
            {
                // newPayloadV5 keeps the V4 parameter shape; the new fields (BlockAccessList, SlotNumber)
                // ride along inside ExecutionPayloadV4 itself, not as extra positional args.
                //
                // NB: pass through `executionRequests` *exactly* as engine_getPayloadV6 returned it.
                // If Pectra (EIP-6110/7002/7251) isn't active, the producer leaves
                // block.Header.RequestsHash null and the V6 result omits executionRequests.
                // Substituting an empty array here would make the validator compute
                // RequestsHash = Keccak("") and reject the very block it just produced
                // ("Invalid block hash ... does not match calculated hash ...").
                JsonNode executionRequests = getPayloadResult["executionRequests"];
                newPayloadParams = new object[] { executionPayload, Array.Empty<object>(), "0x0000000000000000000000000000000000000000000000000000000000000000", executionRequests! };
            }

            JsonNode newPayloadResult = await SendEngineRequestAsync(httpClient, $"engine_newPayloadV{newPayloadVersion}", newPayloadParams);
            string newPayloadStatus = newPayloadResult["status"].GetValue<string>();
            if (newPayloadStatus != "VALID" && newPayloadStatus != "SYNCING" && newPayloadStatus != "ACCEPTED")
            {
                // Surface validationError verbatim — under EIP-7928 we've seen producer/validator
                // disagree on the block hash for the same payload, and the raw error makes that
                // diagnosable without a debugger.
                throw new Exception($"engine_newPayloadV{newPayloadVersion} rejected the payload: {newPayloadResult.ToJsonString()}");
            }

            string newBlockHash = executionPayload["blockHash"].GetValue<string>();
            var finalForkchoiceState = new
            {
                headBlockHash = newBlockHash,
                safeBlockHash = newBlockHash,
                finalizedBlockHash = newBlockHash
            };
            JsonNode fcuResult2 = await SendEngineRequestAsync(httpClient, $"engine_forkchoiceUpdatedV{fcuVersion}", finalForkchoiceState);
            fcuResult2["payloadStatus"]["status"].GetValue<string>().Should().BeOneOf("VALID", "SYNCING", "ACCEPTED");
        }
    }
}
