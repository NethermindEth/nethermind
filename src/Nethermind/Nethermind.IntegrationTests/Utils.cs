using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.IntegrationTests;

public static class Utils
{
    private const string DefaultLocalImageTag = "nethermind:integration-tests";
    private const string DefaultProxyImageTag = "engine-api-proxy:integration-tests";

    private const string DefaultLighthouseImage = "sigp/lighthouse:v7.0.1";
    private const string DefaultGenesisGeneratorImage = "ethpandaops/ethereum-genesis-generator:4.0.0";
    private const string DefaultValidatorKeysImage = "protolambda/eth2-val-tools:0.2.1";

    /// <summary>Mnemonic shared by the genesis generator and the validator keystore generator.</summary>
    public const string DevnetMnemonic = "sleep moment list remain like wall lake industry canvas wonder ecology elite duck salad naive syrup frame brass utility club odor country obey pudding";

    /// <summary>Chain ID for the generated single-node devnet.</summary>
    public const int DevnetChainId = 1337;

    private static readonly SemaphoreSlim s_imageBuildLock = new(1, 1);
    private static readonly SemaphoreSlim s_proxyImageBuildLock = new(1, 1);
    private static IFutureDockerImage s_builtImage;
    private static IFutureDockerImage s_builtProxyImage;

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
    /// Resolves the EngineApiProxy container image to use for integration tests.
    /// If <c>ENGINE_API_PROXY_IMAGE</c> is set, it takes precedence and is used as-is.
    /// Otherwise the repository's <c>tools/EngineApiProxy/Dockerfile</c> is built (once per
    /// test process) and tagged with <c>ENGINE_API_PROXY_IMAGE_TAG</c>
    /// (default <c>engine-api-proxy:integration-tests</c>).
    /// </summary>
    public static async Task<string> GetEngineApiProxyImageAsync()
    {
        string overrideImage = Environment.GetEnvironmentVariable("ENGINE_API_PROXY_IMAGE");
        if (!string.IsNullOrWhiteSpace(overrideImage))
        {
            return overrideImage;
        }

        string tag = Environment.GetEnvironmentVariable("ENGINE_API_PROXY_IMAGE_TAG") ?? DefaultProxyImageTag;

        await s_proxyImageBuildLock.WaitAsync();
        try
        {
            if (s_builtProxyImage is null)
            {
                IFutureDockerImage image = new ImageFromDockerfileBuilder()
                    .WithDockerfileDirectory(CommonDirectoryPath.GetGitDirectory(), string.Empty)
                    .WithDockerfile("tools/EngineApiProxy/Dockerfile")
                    .WithName(tag)
                    .WithCleanUp(false)
                    .Build();
                await image.CreateAsync();
                s_builtProxyImage = image;
            }
        }
        finally
        {
            s_proxyImageBuildLock.Release();
        }

        return s_builtProxyImage.FullName;
    }

    /// <summary>
    /// Builds a <see cref="ContainerBuilder"/> pre-configured for an EngineApiProxy container:
    /// uses the resolved proxy image and passes <paramref name="command"/> as the proxy CLI args.
    /// Callers add networks, port bindings, and wait strategies as needed for their scenario.
    /// </summary>
    public static async Task<ContainerBuilder> BuildEngineApiProxyContainerAsync(string[] command)
    {
        string image = await GetEngineApiProxyImageAsync();
        return new ContainerBuilder()
            .WithImage(image)
            .WithCommand(command);
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
    /// <see cref="DelegatingHandler"/> that stamps a freshly minted JWT onto every outgoing Engine API
    /// request. The Engine spec (§7.1) requires <c>iat</c> within 60 s of the server clock, so caching
    /// the token on <c>HttpClient.DefaultRequestHeaders.Authorization</c> breaks any test that runs
    /// longer than a minute.
    /// </summary>
    public sealed class JwtAuthHandler(string jwtSecretHex) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateJwtToken(jwtSecretHex));
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Builds an <see cref="HttpClient"/> targeting the Engine API at <paramref name="baseUrl"/> with a
    /// <see cref="JwtAuthHandler"/> that re-signs the bearer token per request.
    /// </summary>
    public static HttpClient CreateEngineHttpClient(Uri baseUrl, string jwtSecretHex)
    {
        HttpClient client = new(new JwtAuthHandler(jwtSecretHex) { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = baseUrl,
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
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

    public static Task<JsonNode> SendJsonRpcRequestAsync(HttpClient httpClient, string method, params object[] parameters)
        => SendRawRequestAsync(httpClient, "JSON-RPC", method, parameters);

    public static Task<JsonNode> SendEngineRequestAsync(HttpClient httpClient, string method, params object[] parameters)
        => SendRawRequestAsync(httpClient, "Engine API", method, parameters);

    private static async Task<JsonNode> SendRawRequestAsync(HttpClient httpClient, string errorContext, string method, object[] parameters)
    {
        var request = new
        {
            jsonrpc = "2.0",
            method,
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
            throw new Exception($"{errorContext} error on {method}: {json["error"]}\nRequest: {jsonString}");
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
            JsonNode latestBlock = await SendEngineRequestAsync(httpClient, "eth_getBlockByNumber", "latest", false)
                ?? throw new Exception("Latest block is null.");
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
                string parentGasLimit = latestBlock["gasLimit"].GetValue<string>();
                payloadAttributes = new
                {
                    timestamp = timestampHex,
                    prevRandao = "0x0000000000000000000000000000000000000000000000000000000000000000",
                    suggestedFeeRecipient = "0x0000000000000000000000000000000000000000",
                    withdrawals = Array.Empty<object>(),
                    parentBeaconBlockRoot = "0x0000000000000000000000000000000000000000000000000000000000000000",
                    slotNumber = $"0x{parentNumber + i + 1:x}",
                    targetGasLimit = parentGasLimit
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
            Assert.That(payloadId, Is.Not.Null.And.Not.Empty);

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
            Assert.That(fcuResult2["payloadStatus"]["status"].GetValue<string>(), Is.AnyOf("VALID", "SYNCING", "ACCEPTED"));
        }
    }

    private static string ResolveImage(string overrideEnvVar, string defaultImage)
    {
        string overrideImage = Environment.GetEnvironmentVariable(overrideEnvVar);
        return string.IsNullOrWhiteSpace(overrideImage) ? defaultImage : overrideImage;
    }

    /// <summary>Lighthouse beacon/validator image (override with <c>LIGHTHOUSE_IMAGE</c>).</summary>
    public static string GetLighthouseImage() => ResolveImage("LIGHTHOUSE_IMAGE", DefaultLighthouseImage);

    /// <summary>Genesis generator image (override with <c>GENESIS_GENERATOR_IMAGE</c>).</summary>
    public static string GetGenesisGeneratorImage() => ResolveImage("GENESIS_GENERATOR_IMAGE", DefaultGenesisGeneratorImage);

    /// <summary>Validator keystore generator image (override with <c>VALIDATOR_KEYS_IMAGE</c>).</summary>
    public static string GetValidatorKeysImage() => ResolveImage("VALIDATOR_KEYS_IMAGE", DefaultValidatorKeysImage);

    /// <summary>
    /// Host paths to a freshly generated single-node post-merge devnet: the patched Nethermind
    /// chainspec, the shared JWT secret, the Lighthouse <c>--testnet-dir</c> (CL <c>config.yaml</c>
    /// + <c>genesis.ssz</c>), and the validator keystores/secrets derived from <see cref="DevnetMnemonic"/>.
    /// </summary>
    public sealed record DevnetGenesis(
        string RootDir,
        string ChainspecHostPath,
        string JwtHostPath,
        string TestnetDirHostPath,
        string ValidatorKeysHostPath,
        string ValidatorSecretsHostPath);

    /// <summary>
    /// Bootstraps a single-node devnet whose EL and CL share a genesis, anchored to "now" so the CL
    /// proposes immediately. Runs the ethereum-genesis-generator and eth2-val-tools images against a
    /// shared data dir to produce chainspec, genesis.ssz, JWT, and validator keystores, then patches
    /// <c>params.blobSchedule</c> via <see cref="PatchChainspecBlobSchedule"/>.
    /// </summary>
    public static async Task<DevnetGenesis> GenerateDevnetGenesisAsync(
        int validatorCount = 4,
        int genesisDelaySeconds = 12,
        int secondsPerSlot = 12,
        IReadOnlyList<(string Address, string Balance)> premine = null)
    {
        string root = Path.Combine(Path.GetTempPath(), $"nethermind-lh-{Guid.NewGuid():N}");
        string dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDir);

        long genesisTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        const string genesisDoneMarker = "NETHERMIND_GENESIS_DONE";
        ContainerBuilder generatorBuilder = new ContainerBuilder()
            .WithImage(GetGenesisGeneratorImage())
            .WithEnvironment("PRESET_BASE", "mainnet")
            .WithEnvironment("CHAIN_ID", DevnetChainId.ToString())
            .WithEnvironment("NUMBER_OF_VALIDATORS", validatorCount.ToString())
            .WithEnvironment("GENESIS_TIMESTAMP", genesisTimestamp.ToString())
            .WithEnvironment("GENESIS_DELAY", genesisDelaySeconds.ToString())
            .WithEnvironment("SLOT_DURATION_IN_SECONDS", secondsPerSlot.ToString())
            .WithEnvironment("CAPELLA_FORK_EPOCH", "0")
            .WithEnvironment("DENEB_FORK_EPOCH", "0")
            // Electra disabled -> single (cancun) blobSchedule entry, simplest V3 devnet.
            .WithEnvironment("ELECTRA_FORK_EPOCH", "18446744073709551615")
            .WithEnvironment("EL_AND_CL_MNEMONIC", DevnetMnemonic);

        if (premine is { Count: > 0 })
        {
            // The generator substitutes ${EL_PREMINE_ADDRS} into the EL allocations, so funding a
            // known EOA only needs a one-line JSON object: {"0xaddr":{"balance":"1000ETH"},...}.
            string premineJson = "{" + string.Join(",", premine.Select(p => $"\"{p.Address}\":{{\"balance\":\"{p.Balance}\"}}")) + "}";
            generatorBuilder = generatorBuilder.WithEnvironment("EL_PREMINE_ADDRS", premineJson);
        }

        IContainer generator = generatorBuilder
            .WithBindMount(dataDir, "/data", AccessMode.ReadWrite)
            // Wrap the image entrypoint so we can emit an unambiguous completion marker the wait
            // strategy can latch onto even though the container exits as soon as generation finishes.
            .WithEntrypoint("/bin/bash", "-c")
            .WithCommand($"/work/entrypoint.sh all && echo {genesisDoneMarker}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(genesisDoneMarker))
            .Build();
        await generator.StartAsync();
        await generator.DisposeAsync();

        const string keysDoneMarker = "NETHERMIND_KEYS_DONE";
        IContainer keygen = new ContainerBuilder()
            .WithImage(GetValidatorKeysImage())
            .WithBindMount(dataDir, "/data", AccessMode.ReadWrite)
            .WithEntrypoint("/bin/sh", "-c")
            .WithCommand($"/app/eth2-val-tools keystores --source-mnemonic '{DevnetMnemonic}' --source-min 0 --source-max {validatorCount} --out-loc /data/validators && echo {keysDoneMarker}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged(keysDoneMarker))
            .Build();
        await keygen.StartAsync();
        await keygen.DisposeAsync();

        string metadataDir = Path.Combine(dataDir, "metadata");
        string patchedChainspec = Path.Combine(root, "chainspec.json");
        PatchChainspecBlobSchedule(Path.Combine(metadataDir, "chainspec.json"), patchedChainspec);

        return new DevnetGenesis(
            RootDir: root,
            ChainspecHostPath: patchedChainspec,
            JwtHostPath: Path.Combine(dataDir, "jwt", "jwtsecret"),
            TestnetDirHostPath: metadataDir,
            ValidatorKeysHostPath: Path.Combine(dataDir, "validators", "keys"),
            ValidatorSecretsHostPath: Path.Combine(dataDir, "validators", "secrets"));
    }

    /// <summary>
    /// Rewrites <c>params.blobSchedule</c> from the generator's fork-keyed object
    /// (<c>{ "cancun": { target, max, baseFeeUpdateFraction } }</c>) into the array of
    /// <c>{ timestamp, target, max, baseFeeUpdateFraction }</c> entries Nethermind's chainspec
    /// loader expects. All forks activate at genesis, so each entry is stamped at timestamp 0.
    /// </summary>
    private static void PatchChainspecBlobSchedule(string sourcePath, string destPath)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(sourcePath))
            ?? throw new InvalidOperationException($"Could not parse generated chainspec at {sourcePath}");

        if (root["params"] is JsonObject paramsObj && paramsObj["blobSchedule"] is JsonObject schedule)
        {
            JsonArray converted = [];
            foreach (KeyValuePair<string, JsonNode> entry in schedule)
            {
                if (entry.Value is not JsonObject settings)
                {
                    continue;
                }

                JsonObject clone = (JsonObject)settings.DeepClone();
                clone["timestamp"] = "0x0";
                converted.Add(clone);
            }

            paramsObj["blobSchedule"] = converted;
        }

        File.WriteAllText(destPath, root.ToJsonString());
    }

    /// <summary>
    /// Builds a Lighthouse beacon node configured to drive the given execution endpoint (the proxy).
    /// <c>--always-prepare-payload</c> makes every FCU carry payload attributes, which is what the
    /// proxy's Lighthouse mode intercepts. Single-node devnet flags disable peer discovery/scoring.
    /// </summary>
    public static ContainerBuilder BuildLighthouseBeaconContainer(
        DevnetGenesis genesis,
        INetwork network,
        string networkAlias,
        string executionEndpoint,
        string feeRecipient) =>
        new ContainerBuilder()
            .WithImage(GetLighthouseImage())
            .WithNetwork(network)
            .WithNetworkAliases(networkAlias)
            // Expose the beacon HTTP API so tests can query the CL's canonical head from the host.
            .WithPortBinding(5052, true)
            .WithBindMount(genesis.TestnetDirHostPath, "/testnet", AccessMode.ReadOnly)
            .WithBindMount(genesis.JwtHostPath, "/jwt.hex", AccessMode.ReadOnly)
            .WithCommand(
                "lighthouse", "bn",
                "--testnet-dir", "/testnet",
                "--execution-endpoint", executionEndpoint,
                "--execution-jwt", "/jwt.hex",
                "--always-prepare-payload",
                "--http", "--http-address", "0.0.0.0",
                "--suggested-fee-recipient", feeRecipient,
                "--disable-peer-scoring",
                "--enable-private-discovery",
                "--disable-packet-filter",
                "--target-peers", "0",
                "--allow-insecure-genesis-sync")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("HTTP API started"));

    /// <summary>
    /// Builds a Lighthouse validator client for the devnet's genesis validators. The keystores
    /// directory is mounted read-write because Lighthouse writes <c>validator_definitions.yml</c>
    /// into it on startup.
    /// </summary>
    public static ContainerBuilder BuildLighthouseValidatorContainer(
        DevnetGenesis genesis,
        INetwork network,
        string beaconNodeUrl,
        string feeRecipient) =>
        new ContainerBuilder()
            .WithImage(GetLighthouseImage())
            .WithNetwork(network)
            .WithBindMount(genesis.TestnetDirHostPath, "/testnet", AccessMode.ReadOnly)
            .WithBindMount(genesis.ValidatorKeysHostPath, "/keys", AccessMode.ReadWrite)
            .WithBindMount(genesis.ValidatorSecretsHostPath, "/secrets", AccessMode.ReadOnly)
            .WithCommand(
                "lighthouse", "vc",
                "--testnet-dir", "/testnet",
                "--validators-dir", "/keys",
                "--secrets-dir", "/secrets",
                "--beacon-nodes", beaconNodeUrl,
                "--init-slashing-protection",
                "--suggested-fee-recipient", feeRecipient)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Validator exists in beacon chain"));

    /// <summary>
    /// Polls <c>eth_blockNumber</c> until the head reaches <paramref name="minBlockNumber"/> or
    /// <paramref name="timeout"/> elapses. Returns the last observed block number (which may be
    /// below the target on timeout, letting the caller produce a useful assertion message).
    /// </summary>
    public static async Task<long> WaitForElBlockNumberAsync(HttpClient client, long minBlockNumber, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        long last = 0;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                JsonNode result = await SendJsonRpcRequestAsync(client, "eth_blockNumber");
                last = Convert.ToInt64(result.GetValue<string>()[2..], 16);
                if (last >= minBlockNumber)
                {
                    return last;
                }
            }
            catch (Exception ex)
            {
                TestContext.Progress.WriteLine($"WaitForElBlockNumberAsync poll failed: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return last;
    }

    /// <summary>
    /// Builds, signs, and submits an EIP-4844 (Type-3) blob transaction via
    /// <c>eth_sendRawTransaction</c>, returning the transaction hash. Uses V0 (one proof per blob)
    /// commitments/proofs — the Cancun/Deneb form the EL expects — computed from Nethermind's KZG
    /// trusted setup. The nonce is read from the node so the helper is reusable.
    /// </summary>
    public static async Task<string> SendBlobTransactionAsync(HttpClient httpClient, PrivateKey signer, ulong chainId, Address to, int blobCount = 1)
    {
        if (!KzgPolynomialCommitments.IsInitialized)
        {
            await KzgPolynomialCommitments.InitializeAsync();
        }

        // A blob is a fixed 4096 field elements × 32 bytes. Leaving them zero except a small leading
        // byte keeps each element below the BLS modulus, so the blob is a valid KZG input.
        const int bytesPerBlob = 4096 * 32;
        byte[][] blobs = Enumerable.Range(1, blobCount).Select(i =>
        {
            byte[] blob = new byte[bytesPerBlob];
            blob[0] = (byte)i;
            return blob;
        }).ToArray();

        IBlobProofsManager proofsManager = IBlobProofsManager.For(ProofVersion.V0);
        ShardBlobNetworkWrapper wrapper = proofsManager.AllocateWrapper(blobs);
        proofsManager.ComputeProofsAndCommitments(wrapper);
        byte[][] blobVersionedHashes = proofsManager.ComputeHashes(wrapper);

        JsonNode nonceResult = await SendJsonRpcRequestAsync(httpClient, "eth_getTransactionCount", signer.Address.ToString(), "pending");
        ulong nonce = (ulong)Convert.ToInt64(nonceResult.GetValue<string>()[2..], 16);

        Transaction tx = new()
        {
            Type = TxType.Blob,
            ChainId = chainId,
            Nonce = nonce,
            To = to,
            GasLimit = 21000,
            Value = UInt256.Zero,
            GasPrice = 1_000_000_000UL, // maxPriorityFeePerGas: 1 gwei
            DecodedMaxFeePerGas = 100_000_000_000UL, // maxFeePerGas: 100 gwei, comfortably above base fee
            MaxFeePerBlobGas = 1_000_000_000UL, // 1 gwei, above the devnet's minimum blob base fee
            BlobVersionedHashes = blobVersionedHashes,
            NetworkWrapper = wrapper,
        };

        new EthereumEcdsa(chainId).Sign(signer, tx, isEip155Enabled: true);

        // eth_sendRawTransaction expects the EIP-4844 network ("mempool") form: the typed payload
        // followed by the blobs/commitments/proofs wrapper, un-wrapped from a byte string.
        Rlp rlp = TxDecoder.Instance.Encode(tx, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping);
        string raw = "0x" + rlp.Bytes.ToHexString();

        JsonNode result = await SendJsonRpcRequestAsync(httpClient, "eth_sendRawTransaction", raw);
        return result.GetValue<string>();
    }

    /// <summary>
    /// Polls <c>eth_getTransactionReceipt</c> until a receipt is available (the transaction was
    /// mined into a block) or <paramref name="timeout"/> elapses. Returns the receipt node, or
    /// <c>null</c> on timeout.
    /// </summary>
    public static async Task<JsonNode> WaitForReceiptAsync(HttpClient httpClient, string txHash, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                JsonNode receipt = await SendJsonRpcRequestAsync(httpClient, "eth_getTransactionReceipt", txHash);
                if (receipt is not null)
                {
                    return receipt;
                }
            }
            catch (Exception ex)
            {
                TestContext.Progress.WriteLine($"WaitForReceiptAsync poll failed: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return null;
    }
}
