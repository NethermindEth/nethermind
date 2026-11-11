#:project ../../src/Nethermind/Nethermind.Merge.Plugin/Nethermind.Merge.Plugin.csproj
#:property PublishAot=false

// EIP-8288 devnet driver on the current frame-transaction and wrapper formats.
//   send <el-rpc> <fixtures-dir> <out-dir>
//       Builds a frame transaction with a leanSig dependency bound to its signature hash and a generic leanSTARK
//       dependency, checks that corrupted wrappers are rejected, then submits the valid one with eth_sendProofWrapper.
//       The secp256k1 sender key is read from DEVNET_SENDER_KEY.
//   replay <beacon-api> <engine-rpc> <jwt-file> <out-dir> <proof-slot>
//       Imports every canonical payload from the beacon node into an empty EL over the Engine API. At <proof-slot> it
//       first sends tampered copies of the payload, each re-hashed so only the proof or its dependencies are wrong.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var json = new EthereumJsonSerializer();

return args[0] switch
{
    "send" => await Send(args[1], args[2], args[3]),
    "replay" => await Replay(args[1], args[2], args[3], args[4], ulong.Parse(args[5])),
    _ => throw new ArgumentException($"unknown command {args[0]}"),
};

async Task<int> Send(string rpc, string fixtures, string outDir)
{
    var key = new PrivateKey(Environment.GetEnvironmentVariable("DEVNET_SENDER_KEY") ?? throw new ArgumentException("DEVNET_SENDER_KEY"));
    var chainId = ulong.Parse((await Rpc(rpc, "eth_chainId"))!.GetValue<string>()[2..], System.Globalization.NumberStyles.HexNumber);
    var nonce = (ulong)UInt256.Parse((await Rpc(rpc, "eth_getTransactionCount", key.Address.ToString(), "pending"))!.GetValue<string>());

    var stark = new FrameDependency(Eip8288Constants.LeanStarkScheme,
        new ValueHash256(File.ReadAllBytes(Path.Combine(fixtures, "stark-message.bin"))), new ValueHash256(File.ReadAllBytes(Path.Combine(fixtures, "stark-key.bin"))));
    var sphincsKey = new ValueHash256(File.ReadAllBytes(Path.Combine(fixtures, "sphincs-key.bin")));
    // A zero data hash binds the leanSig dependency to the transaction's own signature hash.
    var unbound = new FrameDependency(Eip8288Constants.LeanSphincsScheme, default, sphincsKey);
    TxFrame[] frames =
    [
        new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, null, 100_000, UInt256.Zero, default),
        new(FrameMode.DepVerify, FrameFlags.None, null, unbound.VerificationGas + stark.VerificationGas, UInt256.Zero,
            Eip8288Dependencies.Serialize([unbound, stark])),
    ];
    var gasLimit = FrameTxValidation.TotalGasLimit(frames);
    var tx = new Transaction
    {
        Type = TxType.FrameTx,
        ChainId = chainId,
        NonceKeys = [],
        Nonce = nonce,
        SenderAddress = key.Address,
        Frames = frames,
        GasLimit = gasLimit,
        GasPrice = 1.GWei,
        MaxFee = (UInt256)gasLimit * 100.GWei,
    };
    tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, key.Address, default, new byte[TxFrameSignature.Secp256k1SignatureLength])];
    var signature = new Ecdsa().Sign(key, FrameTxSigHash.ComputeValue(tx));
    var raw = new byte[TxFrameSignature.Secp256k1SignatureLength];
    raw[0] = signature.RecoveryId;
    signature.Bytes.CopyTo(raw.AsSpan(1));
    tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, key.Address, default, raw)];
    tx.Hash = tx.CalculateHash();

    var sigHash = FrameTxSigHash.ComputeValue(tx);
    var deps = Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue);
    var leanSig = await SignMessage(fixtures, sigHash);
    byte[][] witnesses = [.. deps.Select(d => d.Scheme == Eip8288Constants.LeanStarkScheme ? File.ReadAllBytes(Path.Combine(fixtures, "stark-proof.bin")) : leanSig)];

    byte[] Wrap(Transaction t, byte[][] proofs) => MempoolWrapperDecoder.Instance.Encode(
        new MempoolWrapper { Transactions = [t], Mode = MempoolWrapper.ModeDirect, Deps = deps, Proofs = proofs }).Bytes;
    byte[][] With(int index, byte[] witness) { var copy = (byte[][])witnesses.Clone(); copy[index] = witness; return copy; }
    var sphincsIndex = deps.FindIndex(d => d.Scheme == Eip8288Constants.LeanSphincsScheme);
    var flipped = (byte[])leanSig.Clone();
    flipped[^1] ^= 0xff;
    var badSecp = new Transaction();
    tx.CopyTo(badSecp);
    var badRaw = (byte[])raw.Clone();
    badRaw[^1] ^= 0xff;
    badSecp.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, key.Address, default, badRaw)];
    badSecp.Hash = badSecp.CalculateHash();

    var negatives = new Dictionary<string, byte[]>
    {
        ["leansig-witness-corrupted"] = Wrap(tx, With(sphincsIndex, flipped)),
        ["leansig-witness-for-other-message"] = Wrap(tx, With(sphincsIndex, await SignMessage(fixtures, ValueKeccak.Compute("other message")))),
        ["leanstark-witness-swapped-for-leansig"] = Wrap(tx, With(1 - sphincsIndex, leanSig)),
        ["secp256k1-signature-corrupted"] = Wrap(badSecp, witnesses),
    };
    var results = new JsonObject();
    var failures = 0;
    foreach (var (name, wrapper) in negatives)
    {
        var response = await RpcRaw(rpc, "eth_sendProofWrapper", wrapper.ToHexString(true));
        var rejected = response["error"] is not null;
        failures += rejected ? 0 : 1;
        results[name] = new JsonObject { ["rejected"] = rejected, ["response"] = response.DeepClone() };
        Console.WriteLine($"{(rejected ? "rejected" : "ACCEPTED")} {name}: {response.ToJsonString()}");
    }

    var valid = Wrap(tx, witnesses);
    var accepted = await RpcRaw(rpc, "eth_sendProofWrapper", valid.ToHexString(true));
    Console.WriteLine($"valid wrapper: {accepted.ToJsonString()}");
    failures += accepted["error"] is null ? 0 : 1;

    Directory.CreateDirectory(outDir);
    File.WriteAllText(Path.Combine(outDir, "send.json"), new JsonObject
    {
        ["sender"] = key.Address.ToString(),
        ["chainId"] = chainId,
        ["nonce"] = nonce.ToString(),
        ["txHash"] = tx.Hash.ToString(),
        ["txRlp"] = Rlp.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(true),
        ["signatureHash"] = sigHash.ToString(),
        ["deps"] = new JsonArray([.. deps.Select(d => (JsonNode)new JsonObject
        {
            ["scheme"] = d.Scheme, ["dataHash"] = d.DataHash.ToString(), ["vk"] = d.VerificationKey.ToString(),
        })]),
        ["txDepsHash"] = Eip8288Dependencies.ComputeDepsHash(deps).ToString(),
        ["witnessBytes"] = new JsonArray([.. witnesses.Select(w => (JsonNode)w.Length)]),
        ["wrapper"] = valid.ToHexString(true),
        ["response"] = accepted.DeepClone(),
        ["negatives"] = results,
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return failures;
}

async Task<int> Replay(string beacon, string engine, string jwtFile, string outDir, ulong proofSlot)
{
    var secret = Convert.FromHexString(File.ReadAllText(jwtFile).Trim().Replace("0x", ""));
    var head = ulong.Parse(JsonNode.Parse(await http.GetStringAsync($"{beacon}/eth/v1/beacon/headers/head"))!["data"]!["header"]!["message"]!["slot"]!.GetValue<string>());
    var log = new JsonArray();
    var failures = 0;
    for (ulong slot = 1; slot <= head; slot++)
    {
        using var response = await http.GetAsync($"{beacon}/eth/v2/beacon/blocks/{slot}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
        var message = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!["message"]!;
        var body = message["body"]!;
        foreach (var kind in new[] { "deposits", "withdrawals", "consolidations" })
            if (body["execution_requests"]![kind]!.AsArray().Count != 0) throw new NotSupportedException($"slot {slot} carries execution requests");
        JsonArray hashes = [.. body["blob_kzg_commitments"]!.AsArray().Select(c =>
        {
            var digest = SHA256.HashData(Convert.FromHexString(c!.GetValue<string>()[2..]));
            digest[0] = 1;
            return (JsonNode)digest.ToHexString(true);
        })];
        var parentRoot = message["parent_root"]!.GetValue<string>();
        var payload = EnginePayload(body["execution_payload"]!);

        if (slot == proofSlot)
        {
            var proof = Bytes.FromHexString(payload["starkProof"]!.GetValue<string>());
            var corrupted = (byte[])proof.Clone();
            corrupted[corrupted.Length / 2] ^= 0x01;
            var tampered = new Dictionary<string, JsonObject>
            {
                ["proof-byte-flipped"] = Rehashed(payload, parentRoot, p => p["starkProof"] = corrupted.ToHexString(true)),
                ["proof-truncated"] = Rehashed(payload, parentRoot, p => p["starkProof"] = proof[..^1].ToHexString(true)),
                ["proof-emptied"] = Rehashed(payload, parentRoot, p => p["starkProof"] = "0x"),
                ["proof-kept-transactions-dropped"] = Rehashed(payload, parentRoot, p => p["transactions"] = new JsonArray()),
            };
            foreach (var (name, variant) in tampered)
            {
                var result = await Engine(engine, secret, "engine_newPayloadV4", variant, hashes.DeepClone(), parentRoot, new JsonArray());
                var status = result["result"]?["status"]?.GetValue<string>();
                failures += status == "INVALID" ? 0 : 1;
                Console.WriteLine($"slot {slot} {name}: {result.ToJsonString()}");
                log.Add(new JsonObject { ["slot"] = slot, ["case"] = name, ["blockHash"] = variant["blockHash"]!.DeepClone(), ["response"] = result.DeepClone() });
            }
        }

        var started = Stopwatch.GetTimestamp();
        var imported = await Engine(engine, secret, "engine_newPayloadV4", payload, hashes, parentRoot, new JsonArray());
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var hash = payload["blockHash"]!.GetValue<string>();
        var choice = await Engine(engine, secret, "engine_forkchoiceUpdatedV3",
            new JsonObject { ["headBlockHash"] = hash, ["safeBlockHash"] = hash, ["finalizedBlockHash"] = hash }, null);
        var valid = imported["result"]?["status"]?.GetValue<string>() == "VALID" && choice["result"]?["payloadStatus"]?["status"]?.GetValue<string>() == "VALID";
        failures += valid ? 0 : 1;
        var proofBytes = (payload["starkProof"]?.GetValue<string>().Length ?? 2) / 2 - 1;
        if (!valid || proofBytes > 0 || slot % 32 == 0)
            Console.WriteLine($"slot {slot} block {Convert.ToUInt64(payload["blockNumber"]!.GetValue<string>(), 16)} {hash} proof {proofBytes} B newPayload {elapsed:F0} ms: {imported.ToJsonString()}");
        if (proofBytes > 0 || !valid)
            log.Add(new JsonObject { ["slot"] = slot, ["case"] = "genuine", ["blockHash"] = hash, ["proofBytes"] = proofBytes, ["newPayloadMs"] = Math.Round(elapsed), ["response"] = imported.DeepClone() });
    }

    Directory.CreateDirectory(outDir);
    File.WriteAllText(Path.Combine(outDir, "replay.json"), log.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"replayed slots 1..{head}, failures {failures}");
    return failures;

    JsonObject Rehashed(JsonObject payload, string parentRoot, Action<JsonObject> change)
    {
        var copy = payload.DeepClone().AsObject();
        change(copy);
        var parsed = json.Deserialize<ExecutionPayloadV4>(copy.ToJsonString())!;
        parsed.ParentBeaconBlockRoot = new Hash256(parentRoot);
        parsed.ExecutionRequests = [];
        var block = parsed.TryGetBlock();
        if (block.IsError) throw new InvalidOperationException(block.Error);
        copy["blockHash"] = block.Data.Header.CalculateHash().ToString();
        return copy;
    }
}

// Beacon API JSON (snake_case, decimal quantities) to Engine API JSON (camelCase, hex quantities).
static JsonObject EnginePayload(JsonNode beaconPayload)
{
    string[] quantities = ["block_number", "gas_limit", "gas_used", "timestamp", "base_fee_per_gas", "blob_gas_used", "excess_blob_gas", "index", "validator_index", "amount"];
    JsonNode? Convert(string? name, JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(Camel(p.Key), Convert(p.Key, p.Value)))),
        JsonArray a => new JsonArray([.. a.Select(v => Convert(null, v))]),
        _ when name is not null && quantities.Contains(name) => "0x" + UInt256.Parse(node!.GetValue<string>()).ToString("x").TrimStart('0').PadLeft(1, '0'),
        _ => node?.DeepClone(),
    };
    static string Camel(string key) => key == "recursive_stark_proof" ? "starkProof"
        : string.Concat(key.Split('_').Select((part, i) => i == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    return Convert(null, beaconPayload)!.AsObject();
}

async Task<JsonNode> RpcRaw(string url, string method, params object?[] parameters)
{
    var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = JsonSerializer.SerializeToNode(parameters) };
    using var response = await http.PostAsync(url, new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
    return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
}

async Task<JsonNode?> Rpc(string url, string method, params object?[] parameters)
{
    var response = await RpcRaw(url, method, parameters);
    return response["error"] is { } error ? throw new InvalidOperationException($"{method}: {error.ToJsonString()}") : response["result"];
}

async Task<JsonNode> Engine(string url, byte[] secret, string method, params JsonNode?[] parameters)
{
    static string B64(byte[] bytes) => System.Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var unsigned = B64("""{"alg":"HS256","typ":"JWT"}"""u8.ToArray()) + "." + B64(Encoding.UTF8.GetBytes($"{{\"iat\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}"));
    var token = unsigned + "." + B64(HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(unsigned)));
    var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = new JsonArray([.. parameters.Select(p => p?.DeepClone())]) };
    using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json") };
    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await http.SendAsync(message);
    return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
}

static async Task<byte[]> SignMessage(string fixtures, ValueHash256 message)
{
    var output = Path.GetTempFileName();
    try
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(fixtures, "fixtures"), ["--sign-message", Convert.ToHexString(message.Bytes), output]))!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"fixtures exited with {process.ExitCode}");
        return await File.ReadAllBytesAsync(output);
    }
    finally { File.Delete(output); }
}
