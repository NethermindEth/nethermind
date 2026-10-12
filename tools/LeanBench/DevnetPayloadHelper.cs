// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;

namespace Nethermind.Tools.LeanBench;

public static partial class Program
{
    private static void InspectDevnetPayload(string[] args)
    {
        string Value(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith("--" + name + "=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? fallback;
        string input = Path.GetFullPath(Value("devnet-payload", ""));
        string output = Path.GetFullPath(Value("out", "/tmp/lean-devnet-payload.json"));
        string mutation = Value("mutation", "none");
        if (mutation is not ("none" or "proof" or "commitment")) throw new ArgumentException("Unknown payload mutation");
        if (new FileInfo(input).Length > 64 * 1024 * 1024) throw new ArgumentException("Devnet payload exceeds the helper input bound");
        JsonObject request = JsonNode.Parse(File.ReadAllText(input))?.AsObject() ?? throw new InvalidDataException("Missing Engine request");
        string? method = request["method"]?.GetValue<string>();
        if (method is not ("engine_newPayloadV5" or "engine_newPayloadV6")) throw new InvalidDataException("Expected newPayloadV5 or V6");
        JsonArray parameters = request["params"]?.AsArray() ?? throw new InvalidDataException("Missing Engine parameters");
        if (parameters.Count != (method == "engine_newPayloadV6" ? 5 : 4)) throw new InvalidDataException("Wrong Engine parameter count");
        JsonObject payloadJson = parameters[0]?.AsObject() ?? throw new InvalidDataException("Missing execution payload");
        EthereumJsonSerializer serializer = new();
        ExecutionPayloadV4 payload = serializer.Deserialize<ExecutionPayloadV4>(payloadJson.ToJsonString())
            ?? throw new InvalidDataException("Cannot decode execution payload");
        payload.ParentBeaconBlockRoot = new Hash256(parameters[2]!.GetValue<string>());
        payload.ExecutionRequests = serializer.Deserialize<byte[][]>(parameters[3]!.ToJsonString())
            ?? throw new InvalidDataException("Missing execution requests");
        Result<Block> decoded = payload.TryGetBlock();
        if (decoded.IsError) throw new InvalidDataException(decoded.Error);
        Block block = decoded.Data!;
        Hash256 calculated = block.Header.CalculateHash();
        if (calculated != payload.BlockHash) throw new InvalidDataException("Original payload header hash does not match the complete Engine parameters");
        RecursiveStark proof = block.Header.RecursiveStark ?? throw new InvalidDataException("Payload has no recursive proof");
        ValueHash256 expected = Eip8288Dependencies.ComputeBlockDepsHash(block);
        if (new Hash256(expected) != proof.BlockDepsHash) throw new InvalidDataException("Original dependency commitment does not match transactions");
        NativeLeanProofVerifier verifier = NativeLeanProofVerifier.Instance;
        verifier.EnsureAvailable();
        if (!verifier.VerifyRecursiveStark(expected, Eip8288Constants.AggregatedVk, proof.StarkProof))
            throw new InvalidDataException("Original native block proof failed verification");
        if (mutation == "proof")
        {
            proof.StarkProof[^1] ^= 1;
            payloadJson["recursiveStarkProof"] = "0x" + Convert.ToHexString(proof.StarkProof).ToLowerInvariant();
        }
        else if (mutation == "commitment")
        {
            byte[] commitment = proof.BlockDepsHash.Bytes.ToArray();
            commitment[^1] ^= 1;
            block.Header.RecursiveStark = new(proof.StarkProof, new Hash256(commitment));
            payloadJson["recursiveStarkBlockDepsHash"] = "0x" + Convert.ToHexString(commitment).ToLowerInvariant();
        }
        Hash256 updatedHash = block.Header.CalculateHash();
        payloadJson["blockHash"] = updatedHash.ToString();
        RecursiveStark updatedProof = block.Header.RecursiveStark!;
        bool verified = verifier.VerifyRecursiveStark(new ValueHash256(updatedProof.BlockDepsHash.Bytes),
            Eip8288Constants.AggregatedVk, updatedProof.StarkProof);
        if (verified != (mutation == "none")) throw new InvalidDataException("Payload mutation did not produce the intended proof result");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, request.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        (int signatures, int starks) = Eip8288Dependencies.CountByScheme(Eip8288Dependencies.ForBlock(block));
        object inspection = new
        {
            method,
            mutation,
            originalBlockHash = calculated.ToString(),
            blockHash = updatedHash.ToString(),
            canonicalHeaderHashMatches = true,
            originalNativeProofValid = true,
            resultingNativeProofValid = verified,
            signatures,
            starks,
            proofBytes = proof.StarkProof.Length,
            blockDepsHash = updatedProof.BlockDepsHash.ToString(),
            transactionHashes = block.Transactions.Select(tx => tx.Hash?.ToString())
        };
        File.WriteAllText(output + ".inspection.json", JsonSerializer.Serialize(inspection, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{mutation}: canonical hash {updatedHash}, native proof valid {verified}; {output}");
    }
}
