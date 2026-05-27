// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Ethereum.Test.Base;

/// <summary>
/// Extends <see cref="BlockchainTestBase"/> to drive engine payloads through
/// <c>engine_newPayloadWithWitness</c> (instead of the plain <c>engine_newPayloadVN</c>) for
/// payloads that carry an <c>executionWitness</c> field, and asserts that the returned
/// <see cref="NewPayloadWithWitnessV1Result.ExecutionWitness"/> matches the fixture's expected
/// witness byte-for-byte in order.
/// </summary>
public abstract class WitnessBlockchainTestBase : BlockchainTestBase
{
    // Override: for payloads with an executionWitness, use engine_newPayloadWithWitness instead
    // of engine_newPayloadVN so we can capture and validate the witness.

    protected override async Task<JsonRpcResponse> SendPayloadAsync(
        IJsonRpcService rpcService,
        JsonRpcContext rpcContext,
        TestEngineNewPayloadsJson enginePayload,
        int newPayloadVersion,
        string paramsJson)
    {
        // Payloads without an executionWitness field fall back to the standard path.
        if (enginePayload.ExecutionWitness is null || enginePayload.ExecutionWitnessMutated)
        {
            return await base.SendPayloadAsync(rpcService, rpcContext, enginePayload, newPayloadVersion, paramsJson);
        }

        // Submit through the witness-emitting method.
        JsonRpcResponse witnessResponse = await SendRpc(
            rpcService, rpcContext, "engine_newPayloadWithWitness", paramsJson);

        NewPayloadWithWitnessV1Result? witnessResult = witnessResponse switch
        {
            ResultWrapper<NewPayloadWithWitnessV1Result> { Result.ResultType: Nethermind.Core.ResultType.Success } rw => rw.Data,
            ResultWrapper<NewPayloadWithWitnessV1Result> => null, // failure — pass through
            JsonRpcSuccessResponse { Result: NewPayloadWithWitnessV1Result wr } => wr,
            _ => null
        };

        if (witnessResult is null)
        {
            // Either an RPC error, a ResultWrapper failure, or an unexpected type.
            // Return as-is so TryGetRpcError / GetPayloadStatus in the base class handles it.
            return witnessResponse;
        }

        // Extract status fields before disposing — witnessResult owns the Witness backing buffers.
        PayloadStatusV1 syntheticStatus = new()
        {
            Status = witnessResult.Status,
            LatestValidHash = witnessResult.LatestValidHash,
            ValidationError = witnessResult.ValidationError
        };

        try
        {
            // Witness comparison — only for VALID payloads with a non-mutated fixture witness.
            // AssertWitnessMatchesFixture copies bytes into local lists before comparing,
            // so disposing witnessResult in the finally block is safe.
            if (witnessResult.Status == PayloadStatus.Valid && witnessResult.ExecutionWitness is not null)
            {
                AssertWitnessMatchesFixture(
                    enginePayload.ExecutionWitness.Value,
                    witnessResult.ExecutionWitness,
                    enginePayload);
            }
            else if (witnessResult.Status == PayloadStatus.Valid && witnessResult.ExecutionWitness is null)
            {
                // A VALID payload with a fixture witness must always return a witness.
                Assert.Fail(
                    $"engine_newPayloadWithWitness returned VALID but no witness was included " +
                    $"in the result. Fixture expected a witness for block " +
                    $"{enginePayload.Params[0].GetProperty("blockHash").GetString()}.");
            }
        }
        finally
        {
            witnessResult.Dispose();
        }

        // Synthesise a plain PayloadStatusV1 response so the base-class FCU logic continues
        // to work unmodified (it only cares about the status / latestValidHash fields).
        // JsonRpc is a readonly field initialised to "2.0" — it cannot be set via object
        // initializer (CS0191). Id is a regular settable property and is copied normally.
        return new JsonRpcSuccessResponse
        {
            Result = syntheticStatus,
            Id = witnessResponse.Id,
        };
    }

    private static void AssertWitnessMatchesFixture(
        JsonElement fixtureWitness,
        Nethermind.Consensus.Stateless.Witness actual,
        TestEngineNewPayloadsJson enginePayload)
    {
        string blockHash = enginePayload.Params[0].GetProperty("blockHash").GetString() ?? "<unknown>";

        List<byte[]> expectedState = ReadHexList(fixtureWitness, "state");
        List<byte[]> expectedCodes = ReadHexList(fixtureWitness, "codes");
        List<byte[]> expectedHeaders = ReadHexList(fixtureWitness, "headers");

        List<byte[]> actualState = [.. actual.State];
        List<byte[]> actualCodes = [.. actual.Codes];
        List<byte[]> actualHeaders = [.. actual.Headers];

        List<string> mismatches = [];

        CheckOrderedField("state", expectedState, actualState, mismatches);
        CheckOrderedField("codes", expectedCodes, actualCodes, mismatches);
        CheckOrderedField("headers", expectedHeaders, actualHeaders, mismatches);

        if (mismatches.Count > 0)
        {
            System.Text.StringBuilder sb = new();
            sb.AppendLine($"engine_newPayloadWithWitness witness mismatch for block {blockHash}:");
            foreach (string m in mismatches)
            {
                sb.AppendLine($"  {m}");
            }
            sb.AppendLine("Expected state:");
            for (int i = 0; i < expectedState.Count; i++)
            {
                sb.AppendLine($"  [{i}] 0x{expectedState[i].ToHexString()}");
            }
            sb.AppendLine("Actual state:");
            for (int i = 0; i < actualState.Count; i++)
            {
                sb.AppendLine($"  [{i}] 0x{actualState[i].ToHexString()}");
            }
            sb.AppendLine("Expected codes:");
            for (int i = 0; i < expectedCodes.Count; i++)
            {
                sb.AppendLine($"  [{i}] 0x{expectedCodes[i].ToHexString()}");
            }
            sb.AppendLine("Actual codes:");
            for (int i = 0; i < actualCodes.Count; i++)
            {
                sb.AppendLine($"  [{i}] 0x{actualCodes[i].ToHexString()}");
            }
            Assert.Fail(sb.ToString());
        }
    }

    /// <summary>
    /// Reads a JSON array of 0x-prefixed hex strings from <paramref name="element"/>
    /// under <paramref name="field"/> and returns the raw byte arrays.
    /// Missing fields are treated as empty lists.
    /// </summary>
    private static List<byte[]> ReadHexList(JsonElement element, string field)
    {
        if (!element.TryGetProperty(field, out JsonElement arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<byte[]> result = new(arr.GetArrayLength());
        foreach (JsonElement item in arr.EnumerateArray())
        {
            string? hex = item.GetString();
            if (hex is null) continue;
            result.Add(Bytes.FromHexString(hex));
        }
        return result;
    }

    /// <summary>
    /// Order-sensitive comparison.  Reports all differences so one failing test reveals the
    /// full picture rather than stopping at the first mismatch.
    /// </summary>
    private static void CheckOrderedField(
        string field,
        IReadOnlyList<byte[]> expected,
        IReadOnlyList<byte[]> actual,
        List<string> mismatches)
    {
        if (expected.Count == actual.Count &&
            expected.Zip(actual).All(p => p.First.AsSpan().SequenceEqual(p.Second)))
        {
            return; // exact match
        }

        int common = Math.Min(expected.Count, actual.Count);

        if (expected.Count == actual.Count)
        {
            // Same length but content differs — report first divergence.
            int firstBad = expected.Zip(actual)
                .Select((p, i) => (p, i))
                .First(x => !x.p.First.AsSpan().SequenceEqual(x.p.Second))
                .i;
            mismatches.Add(
                $"{field}: ordered mismatch (both have {expected.Count} items); " +
                $"first difference at index {firstBad}: " +
                $"expected 0x{expected[firstBad].ToHexString()[..Math.Min(16, expected[firstBad].Length * 2)]}…, " +
                $"got 0x{actual[firstBad].ToHexString()[..Math.Min(16, actual[firstBad].Length * 2)]}…");
            return;
        }

        if (expected.Take(common).Zip(actual.Take(common)).All(p => p.First.AsSpan().SequenceEqual(p.Second)))
        {
            // Common prefix matches; just a length difference.
            if (expected.Count > actual.Count)
            {
                mismatches.Add(
                    $"{field}: {expected.Count - actual.Count} missing item(s) " +
                    $"(not emitted by client)");
            }
            else
            {
                mismatches.Add(
                    $"{field}: {actual.Count - expected.Count} extra item(s) " +
                    $"(over-collected by client)");
            }
        }
        else
        {
            mismatches.Add(
                $"{field}: ordered mismatch " +
                $"(expected {expected.Count} items, got {actual.Count})");
        }
    }
}
