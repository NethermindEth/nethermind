// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.Test;

[TestFixture]
public sealed class ExtractorTests
{
    private const string SimpleRefundCall = "GasConsumed spentGas = Refund(tx, header, spec, opts, in substate, in gasAvailable, in opcodeGasPrice, codeInsertRefunds: 0, in floorGas, in standardGas, postIntrinsicStateReservoir);";
    private const string SimpleAccessCall = "ReportSimpleTransferAccess(tx, spec, tracer, recipient);";
    private const string SimpleFeeCall = "UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);";
    private const string SimpleFinalizeCall = "return FinalizeTransaction(tx, spec, tracer, opts, restore, commit, deleteCallerAccount, in senderReservedGasPayment, recipient, in substate, spentGas, statusCode);";

    [Test]
    public async Task Stage_b_replay_compares_complete_observations_from_identical_input_bytes()
    {
        StageBPrefixProgram program = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        (StageBReplayCase Replay, string Tag)[] cases = ReplayCases().ToArray();
        Assert.That(cases, Has.Length.EqualTo(43));
        string[] rows = cases.Select(static item => StageBReplay.Encode(item.Replay)).ToArray();
        (int exit, string output, string error) = await RunLeanReplay(rows);
        Assert.That(exit, Is.Zero, error);
        string[] results = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.That(results, Has.Length.EqualTo(rows.Length));
        Assert.That(cases.Select(static item => item.Replay.Id).Distinct().Count(), Is.EqualTo(cases.Length));
        for (int index = 0; index < rows.Length; index++)
        {
            StageBReplayCase decoded = StageBReplay.Decode(rows[index]);
            JsonArray expected = StageBReplay.Run(program, decoded);
            JsonNode actual = JsonNode.Parse(results[index])!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(expected[3]!.GetValue<string>(), Is.EqualTo(cases[index].Tag), decoded.Id);
                Assert.That(StageBReplay.SameObservation(expected, actual), Is.True,
                    $"{decoded.Id}\nC#: {expected.ToJsonString()}\nLean: {actual.ToJsonString()}");
            }
        }
    }

    [TestCase("shape")]
    [TestCase("unknown-version")]
    [TestCase("previous-version")]
    [TestCase("tracer-four-fields")]
    [TestCase("tracer-six-fields")]
    [TestCase("tracer-access-null")]
    [TestCase("tracer-access-string")]
    [TestCase("program")]
    [TestCase("numeric-json")]
    [TestCase("leading-zero")]
    [TestCase("negative-zero")]
    [TestCase("plus-sign")]
    [TestCase("uint64-overflow")]
    [TestCase("uint256-overflow")]
    [TestCase("int64-overflow")]
    [TestCase("null-required")]
    [TestCase("boolean-string")]
    [TestCase("upper-hex")]
    [TestCase("odd-hex")]
    [TestCase("unknown-request")]
    [TestCase("unknown-reply")]
    [TestCase("extra-reply-field")]
    [TestCase("micro-limit")]
    [TestCase("trailing-json")]
    [TestCase("blank-record")]
    [TestCase("lone-surrogate")]
    [TestCase("lone-low-surrogate")]
    [TestCase("wrong-surrogate-pair")]
    [TestCase("utf8-line-limit")]
    [TestCase("tape-limit")]
    public async Task Stage_b_replay_rejects_bad_schema_on_both_decoders(string mutation)
    {
        JsonArray row = JsonNode.Parse(StageBReplay.Encode(ReplayCases().First().Replay))!.AsArray();
        switch (mutation)
        {
            case "shape": row.Add(false); break;
            case "unknown-version": row[0] = "stage-b-replay-v3"; break;
            case "previous-version": row[0] = "stage-b-replay-v1"; break;
            case "tracer-four-fields": row[5]![2]!.AsArray().RemoveAt(4); break;
            case "tracer-six-fields": row[5]![2]!.AsArray().Add(false); break;
            case "tracer-access-null": row[5]![2]![4] = null; break;
            case "tracer-access-string": row[5]![2]![4] = "true"; break;
            case "program": row[2] = "0"; break;
            case "numeric-json": row[3] = 853; break;
            case "leading-zero": row[3] = "0853"; break;
            case "negative-zero": row[5]![15] = "-0"; break;
            case "plus-sign": row[5]![15] = "+183600"; break;
            case "uint64-overflow": row[5]![0]![4] = "18446744073709551616"; break;
            case "uint256-overflow": row[5]![0]![2] = (BigInteger.One << 256).ToString(); break;
            case "int64-overflow": row[5]![15] = "9223372036854775808"; break;
            case "null-required": row[5]![0]![0] = null; break;
            case "boolean-string": row[5]![1]![0] = "true"; break;
            case "upper-hex": row[5]![0]![3] = "FF"; break;
            case "odd-hex": row[5]![0]![3] = "a"; break;
            case "unknown-request": row[6]![0]![0]![0] = "unknown"; break;
            case "unknown-reply": row[6]![0]![1]![0] = "unknown"; break;
            case "extra-reply-field": row[6]![0]![1]!.AsArray().Add(false); break;
            case "micro-limit": row[4] = "1000001"; break;
            case "utf8-line-limit": row[1] = new string('é', StageBReplay.MaxLineLength / 2 + 1); break;
            case "tape-limit":
                JsonArray tape = row[6]!.AsArray();
                while (tape.Count <= StageBReplay.MaxTapeLength) tape.Add(tape[0]!.DeepClone());
                break;
        }
        string bytes = mutation == "blank-record" ? "" : row.ToJsonString() + (mutation == "trailing-json" ? "[]" : "");
        if (mutation == "lone-surrogate") bytes = bytes.Replace("\"zero\"", "\"\\uD800\"", StringComparison.Ordinal);
        if (mutation == "lone-low-surrogate") bytes = bytes.Replace("\"zero\"", "\"\\uDC00\"", StringComparison.Ordinal);
        if (mutation == "wrong-surrogate-pair") bytes = bytes.Replace("\"zero\"", "\"\\uD800\\u0041\"", StringComparison.Ordinal);
        if (mutation == "utf8-line-limit") bytes = bytes.Replace("\\u00E9", "é", StringComparison.Ordinal);
        Assert.That(() => StageBReplay.Decode(bytes), Throws.Exception);
        (int exit, string output, string error) = await RunLeanReplay([bytes]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exit, Is.Not.Zero, mutation);
            Assert.That(output, Is.Empty, mutation);
            Assert.That(error, Does.Contain("Stage-B replay:"), mutation);
        }
    }

    [Test]
    public void Stage_b_replay_preserves_access_and_actions_independently([Values] bool access, [Values] bool actions)
    {
        StageBReplayCase replay = ReplayCases().First().Replay;
        StageBTracerInput tracer = new(IsTracingState: !access, IsTracingActions: actions,
            IsTracingCode: !actions, IsTracingLogs: !access, IsTracingAccess: access);
        replay = replay with { Input = replay.Input with { Tracer = tracer } };
        string encoded = StageBReplay.Encode(replay);
        JsonArray row = JsonNode.Parse(encoded)!.AsArray();
        StageBReplayCase decoded = StageBReplay.Decode(encoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row[0]!.GetValue<string>(), Is.EqualTo("stage-b-replay-v2"));
            Assert.That(row[5]![2]!.AsArray().Select(static value => value!.GetValue<bool>()),
                Is.EqualTo(new[] { !access, actions, !actions, !access, access }));
            Assert.That(decoded.Input.Tracer, Is.EqualTo(tracer));
            Assert.That(StageBReplay.Encode(decoded), Is.EqualTo(encoded));
        }

        row[5]![2]![1] = access;
        row[5]![2]![4] = actions;
        Assert.That(StageBReplay.Decode(row.ToJsonString()).Input.Tracer,
            Is.EqualTo(tracer with { IsTracingActions = access, IsTracingAccess = actions }));
    }

    [Test]
    public async Task Stage_b_replay_micro_fuel_exhaustion_fails_the_executable()
    {
        StageBReplayCase replay = ReplayCases().First().Replay with { MicroFuel = 0 };
        (int exit, string output, string error) = await RunLeanReplay([StageBReplay.Encode(replay)]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exit, Is.Not.Zero);
            Assert.That(output, Is.Empty);
            Assert.That(error, Does.Contain("micro-fuel exhausted"));
        }
    }

    [Test]
    public void Stage_b_replay_comparator_detects_every_scalar_and_array_mutation()
    {
        string path = Path.Combine(FindRepoRoot(), "tools/Evm/Lean/SimpleTransferCompletionExtractor/StageB/Generated/PrefixProgram.ir.json");
        StageBPrefixProgram program = StageBArtifact.ParseAndValidate(File.ReadAllBytes(path)).Program;
        StageBReplayCase replay = ReplayCases().Single(static item => item.Replay.Id == "suffix").Replay;
        JsonArray observation = StageBReplay.Run(program, StageBReplay.Decode(StageBReplay.Encode(replay)));
        Assert.That(StageBReplay.SameObservation(observation, JsonNode.Parse(observation.ToJsonString())!), Is.True);
        int checkedMutations = 0;
        Check(observation, []);
        Assert.That(checkedMutations, Is.GreaterThan(100));

        void Check(JsonNode? node, int[] indices)
        {
            if (node is JsonArray array)
            {
                JsonNode changed = observation.DeepClone();
                At(changed, indices)!.AsArray().Add("extra");
                Assert.That(StageBReplay.SameObservation(observation, changed), Is.False, string.Join('/', indices));
                checkedMutations++;
                if (array.Count > 0)
                {
                    changed = observation.DeepClone();
                    At(changed, indices)!.AsArray().RemoveAt(0);
                    Assert.That(StageBReplay.SameObservation(observation, changed), Is.False, string.Join('/', indices));
                    checkedMutations++;
                }
                for (int index = 0; index < array.Count; index++) Check(array[index], [.. indices, index]);
            }
            else
            {
                JsonNode changed = observation.DeepClone();
                JsonArray parent = At(changed, indices[..^1])!.AsArray();
                parent[indices[^1]] = node is JsonValue scalar && scalar.TryGetValue(out bool value)
                    ? JsonValue.Create(!value) : JsonValue.Create((node?.ToJsonString() ?? "null") + "-mutation");
                Assert.That(StageBReplay.SameObservation(observation, changed), Is.False, string.Join('/', indices));
                checkedMutations++;
            }
        }

        static JsonNode? At(JsonNode root, int[] indices)
        {
            JsonNode? current = root;
            foreach (int index in indices) current = current![index];
            return current;
        }
    }

    private static IEnumerable<(StageBReplayCase Replay, string Tag)> ReplayCases()
    {
        StageBPostNonceInput baseline = InterpreterInput(7) with
        {
            Spec = new(true, false), Transaction = new("sender", "recipient", 7, [], 300_000),
        };
        yield return Make("zero", baseline with { Transaction = baseline.Transaction with { Value = 0 } });
        yield return Make("existing", baseline);
        yield return Make("dead", baseline, dead: true);
        yield return Make("self", baseline with { Transaction = baseline.Transaction with { Recipient = "sender" } });
        yield return Make("reservoir", baseline with { Transaction = baseline.Transaction with { GasLimit = 17_277_216 } }, dead: true);
        yield return Make("spill", baseline with { Transaction = baseline.Transaction with { GasLimit = 16_827_216 } }, dead: true);
        yield return Make("exact", baseline with { Transaction = baseline.Transaction with { GasLimit = 204_600 } }, dead: true);
        yield return Make("oog", baseline with { Transaction = baseline.Transaction with { GasLimit = 204_599 } }, dead: true, oog: true);
        yield return Make("substate-and-distinct-fields", baseline with
        {
            IntrinsicGas = new(new(21_000, 2, 3, 5, 7), new(22_000, 11, 13, 17, 19)),
            OpcodeGasPrice = (BigInteger.One << 200) + 23, PremiumPerGas = 29, SenderReservedGasPayment = 31,
            BlobBaseFee = 37, DeleteCallerAccount = true,
        }, dead: true);
        yield return Make("unicode-identities", baseline with
        {
            Transaction = baseline.Transaction with { Sender = "s<>\"\\\n😀", Recipient = "ré<[]>", Value = (BigInteger.One << 255) + 17 },
        });
        StageBExchange commit = new StageBUnitExchange(new StageBCommitRequest(false, false));
        foreach ((string id, bool empty, string? delegation) in new (string, bool, string?)[]
                 { ("code-fallback", false, null), ("delegated-fallback", true, "delegate") })
            yield return (new(id, 853, 20_000, baseline,
                [new StageBCodeLookupExchange(new StageBCodeLookupRequest("recipient", false), empty, delegation), commit]), "outside");
        yield return Make("data-with-empty-code",
            baseline with { Transaction = baseline.Transaction with { Data = [0, 127, 255] } });
        StageBReplayCase zero = Make("zero", baseline with { Transaction = baseline.Transaction with { Value = 0 } }).Replay;
        yield return (zero with { Id = "suffix", Tape = [.. zero.Tape, commit,
            new StageBCodeLookupExchange(new StageBAddBalanceRequest("unused", 41), false, "unused-delegate")] }, "suspended");
        yield return (zero with { Id = "wrong-request", Tape = [new StageBUnitExchange(new StageBIsDeadAccountRequest("recipient")), .. zero.Tape] }, "rejected");
        yield return (zero with { Id = "wrong-reply", Tape = [new StageBBoolExchange(new StageBCodeLookupRequest("recipient", false), true), .. zero.Tape] }, "rejected");
        yield return (zero with { Id = "missing-tape", Tape = zero.Tape[..1] }, "rejected");
        foreach (long fuel in new long[] { 1, 495, 496, 497 })
            yield return (zero with { Id = "fuel-" + fuel, Fuel = fuel }, fuel < 496 ? "fuel-exhausted" : "suspended");
        foreach (long fuel in new long[] { 0, 854 })
            yield return (zero with { Id = "invalid-fuel-" + fuel, Fuel = fuel }, "rejected");
        StageBPostNonceInput[] exclusions =
        [
            baseline with { Spec = new(false, false) }, baseline with { Spec = new(true, true) },
            baseline with { Tracer = new(IsTracingState: true) }, baseline with { Tracer = new(IsTracingActions: true) },
            baseline with { Tracer = new(IsTracingCode: true) }, baseline with { Tracer = new(IsTracingLogs: true) },
            baseline with { Restore = true }, baseline with { Commit = false }, baseline with { Warmup = true },
            baseline with { IsCodeOverridable = true }, baseline with { ForceSimpleTransferDisabled = true },
            baseline with { ExecutionGasLimitCap = 1 }, baseline with { NewAccountStateCost = 1 },
            baseline with { Transaction = baseline.Transaction with { HasAuthorizationList = true } },
            baseline with { Transaction = baseline.Transaction with { Recipient = null } },
            baseline with { Transaction = baseline.Transaction with { GasLimit = 20_999 } },
            baseline with { IntrinsicGas = new(new(21_000, -1, 0, 0, 0), baseline.IntrinsicGas.FloorGas) },
            baseline with { IntrinsicGas = new(new(ulong.MaxValue, 1, 0, 0, 0), baseline.IntrinsicGas.FloorGas) },
            baseline with { Transaction = baseline.Transaction with { GasLimit = (ulong)long.MaxValue + 1 } },
            baseline with
            {
                Transaction = baseline.Transaction with { GasLimit = 20_000_000 },
                IntrinsicGas = new(new(16_777_217, 0, 0, 0, 0), baseline.IntrinsicGas.FloorGas),
            },
        ];
        for (int index = 0; index < exclusions.Length; index++)
            yield return (zero with { Id = "excluded-" + index, Input = exclusions[index] }, "rejected");

        static (StageBReplayCase Replay, string Tag) Make(string id, StageBPostNonceInput input, bool dead = false, bool oog = false)
        {
            string recipient = input.Transaction.Recipient!;
            List<StageBExchange> tape =
            [
                new StageBCodeLookupExchange(new StageBCodeLookupRequest(recipient, false), true, null),
                new StageBUnitExchange(new StageBTraceRequest("IncrementEmptyCalls", [])),
            ];
            if (input.Transaction.Value != 0 && input.Transaction.Sender != recipient)
            {
                tape.Add(new StageBBoolExchange(new StageBIsDeadAccountRequest(recipient), dead));
                if (!oog) tape.Add(new StageBUnitExchange(new StageBSubtractBalanceRequest(input.Transaction.Sender, input.Transaction.Value)));
            }
            if (input.Transaction.Sender != recipient && !oog)
                tape.Add(new StageBBoolExchange(new StageBAddBalanceRequest(recipient, input.Transaction.Value), true));
            return (new(id, 853, 20_000, input, tape.ToArray()), "suspended");
        }
    }

    [TestCase(-1L)]
    [TestCase(0L)]
    [TestCase(854L)]
    [TestCase(long.MaxValue)]
    public void Stage_b_prefix_interpreter_rejects_out_of_domain_fuel_before_requests(long fuel)
    {
        string generated = Path.Combine(FindRepoRoot(), "tools/Evm/Lean/SimpleTransferCompletionExtractor/StageB/Generated/PrefixProgram.ir.json");
        StageBPrefixProgram program = StageBArtifact.ParseAndValidate(File.ReadAllBytes(generated)).Program;
        StageBExchange[] exchanges = [new StageBUnitExchange(new StageBCommitRequest(false, false))];
        StageBResponseTape tape = new(exchanges);
        StageBPrefixRun run = StageBPrefixInterpreter.Run(program, InterpreterInput(0), tape, fuel);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.Rejected));
            Assert.That(run.RemainingFuel, Is.EqualTo(fuel));
            Assert.That(run.Requests, Is.Empty);
            Assert.That(tape.Consumed, Is.Zero);
            Assert.That(tape.RemainingExchanges, Is.EqualTo(exchanges));
        }
    }

    private static async Task<(int Exit, string Output, string Error)> RunLeanReplay(string[] rows)
    {
        string package = Path.Combine(FindRepoRoot(), "tools/Evm/Lean/SimpleTransferCompletionExtractor");
        string executable = Path.Combine(package, ".lake/build/bin", OperatingSystem.IsWindows() ? "stage-b-replay.exe" : "stage-b-replay");
        Assert.That(File.Exists(executable), Is.True, "Build the stage-b-replay Lake executable before this test.");
        ProcessStartInfo info = new(executable)
        {
            WorkingDirectory = package, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using Process process = Process.Start(info)!;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(120));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            foreach (string row in rows) await process.StandardInput.WriteAsync((row + "\n").AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [TestCase("internal static class StateGasChargeKernel\n{", "internal static class StateGasChargeKernel\n{\n    private static class Math { public static long Min(long left, long right) => 123; }")]
    [TestCase("    Success,\n    OutOfGas,", "    Probe,\n    Success,\n    OutOfGas,")]
    public void Stage_b_accepted_stage_binding_rejects_same_text_semantic_rebinding(string original, string replacement)
    {
        using SourceFixture fixture = new();
        string entry = fixture.RequireStageBPlan().AcceptedStages.Single(static stage => stage.Name == "state-charge").EntryPoints.Single();
        fixture.ReplaceFirst(Extractor.StateGasChargeKernelPath, original, replacement);
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        Assert.That(() => StageBPrefixCompiler.Compile(fixture.RequireStageBPlan()),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Stage-B accepted-stage-binding: " + entry + "."));
    }

    [Test]
    public void Stage_b_accepted_stage_binding_checks_transitive_helper_resolution()
    {
        using SourceFixture fixture = new();
        string helper = fixture.RequireStageBPlan().AcceptedStages.Single(static stage => stage.Name == "ordinary-refund").SourceClosure
            .Single(static dependency => dependency.Symbol.Contains(".RefundHelper.CalculateClaimableRefund(", StringComparison.Ordinal)).Symbol;
        fixture.ReplaceFirst("src/Nethermind/Nethermind.Evm/RefundHelper.cs", "public static class RefundHelper\n    {",
            "public static class RefundHelper\n    {\n        private static class Math { public static ulong Min(ulong left, ulong right) => 123; }");
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        Assert.That(() => fixture.RequireStageBPlan(),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Stage-B accepted-stage-binding: " + helper + "."));
    }

    [Test]
    public void Stage_b_accepted_stage_binding_allows_unreferenced_nested_helper()
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.StateGasChargeKernelPath, "internal static class StateGasChargeKernel\n{",
            "internal static class StateGasChargeKernel\n{\n    private static class UnusedProbe { public static long Min(long left, long right) => 123; }");
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        Assert.That(PrefixNodes(prefix).Count(static node => node.Call?.Target.Kind == StageBPrefixTargetKind.StateCharge), Is.EqualTo(1));
    }

    [Test]
    public void Stage_b_prefix_compiles_cfg_to_exact_refund_suspension()
    {
        StageBPlan plan = StageBLowering.Build(FindRepoRoot());
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(plan);
        StageBPrefixSuspension refund = prefix.Refund;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refund.Call.Target.Kind, Is.EqualTo(StageBPrefixTargetKind.RefundSuspension));
            Assert.That(refund.Call.Arguments.Select(static argument => argument.Ordinal), Is.EqualTo(Enumerable.Range(0, 12)));
            Assert.That(refund.Call.Arguments.Select(static argument => argument.Mode), Is.EqualTo(new[]
            {
                StageBArgumentMode.Value, StageBArgumentMode.Value, StageBArgumentMode.Value, StageBArgumentMode.Value,
                StageBArgumentMode.ReadOnlyLocation, StageBArgumentMode.ReadOnlyLocation, StageBArgumentMode.ReadOnlyLocation,
                StageBArgumentMode.Value, StageBArgumentMode.ReadOnlyLocation, StageBArgumentMode.ReadOnlyLocation,
                StageBArgumentMode.Value, StageBArgumentMode.Value,
            }));
            Assert.That(refund.Call.Arguments[11].Implicit, Is.True);
            Assert.That(refund.Call.Arguments[11].Kind, Is.EqualTo("DefaultValue"));
            Assert.That(refund.OrderedOperands[refund.Call.Arguments[11].Child].Children[0].Constant, Is.EqualTo("bool:false"));
            Assert.That(refund.Call.ReceiverChild, Is.Zero);
            Assert.That(refund.OrderedOperands[0].Kind, Is.EqualTo(StageBOperationKind.InstanceReference));
            Assert.That(prefix.Functions.Select(static function => function.Signature.Name), Does.Not.Contain("ReportSimpleTransferAccess"));
            Assert.That(prefix.Functions.Select(static function => function.Signature.Name), Does.Not.Contain("PayFees"));
            Assert.That(prefix.FuelBound, Is.Positive);
            Assert.That(prefix.Functions.Single(function => function.Signature.Symbol == plan.PrefixAnchors!.SimpleTransfer).MayReturn, Is.False);
            Assert.That(PrefixNodes(prefix).Count(static node => node.Kind == StageBOperationKind.Return), Is.Zero);
            Assert.That(prefix.Functions.SelectMany(static function => function.Blocks).Any(static block => block.Exit == StageBPrefixExit.Return), Is.True);
            Assert.That(prefix.Functions.SelectMany(static function => function.Blocks).Any(static block => block.Exit == StageBPrefixExit.Suspend), Is.True);
            Assert.That(PrefixNodes(prefix).Any(static node => node.Kind == StageBOperationKind.ObjectCreation), Is.True);
            Assert.That(PrefixNodes(prefix).Any(static node => node.Call?.Target.Kind == StageBPrefixTargetKind.Local), Is.True);
            Assert.That(PrefixNodes(prefix).Any(static node => node.Call?.Target.Kind == StageBPrefixTargetKind.StaticField), Is.True);
            Assert.That(PrefixNodes(prefix).Count(static node => node.Call?.Target.Kind == StageBPrefixTargetKind.Initialization), Is.EqualTo(1));
            Assert.That(PrefixNodes(prefix).Count(static node => node.Call?.Target.Kind == StageBPrefixTargetKind.StateCharge), Is.EqualTo(1));
            Assert.That(prefix.Functions.SelectMany(static function => function.Calls).Intersect(prefix.Initializers), Is.Empty);
            Assert.That(Terms(refund.Continuation.SourceBlocks.SelectMany(static block => block.Operations))
                .Any(static term => term.Symbol.Contains(".ReportSimpleTransferAccess(", StringComparison.Ordinal)), Is.True);
            Assert.That(PrefixNodes(prefix).Any(static node => node.Symbol.Contains(".ReportSimpleTransferAccess(", StringComparison.Ordinal)), Is.False);
            Assert.That(refund.Continuation.PendingOperation.Kind, Is.EqualTo(StageBOperationKind.SimpleAssignment));
            Assert.That(refund.Continuation.CallPath, Is.EqualTo(new[] { 1 }));
            Assert.That(refund.Continuation.Block, Is.EqualTo(32));
            Assert.That(refund.Continuation.Operation, Is.EqualTo(3));
            StageBBlock source32 = refund.Continuation.SourceBlocks.Single(static block => block.Ordinal == 32);
            Assert.That(source32.Kind, Is.EqualTo("Block"));
            Assert.That(source32.Reachable, Is.True);
            Assert.That(source32.ConditionKind, Is.EqualTo("None"));
            Assert.That(source32.Operations, Has.Length.EqualTo(4));
            Assert.That(source32.BranchValue, Is.Null);
            Assert.That(source32.ContainsExcludedOperations, Is.False);
            Assert.That(source32.FallThrough, Is.Not.Null);
            Assert.That(source32.FallThrough!.Destination, Is.EqualTo(33));
            Assert.That(source32.FallThrough.Semantics, Is.EqualTo("Regular"));
            Assert.That(source32.FallThrough.LeavingRegions, Is.Empty);
            Assert.That(source32.FallThrough.EnteringRegions, Is.EqualTo(new[] { 5 }));
            Assert.That(source32.FallThrough.FinallyRegions, Is.Empty);
            Assert.That(source32.Conditional, Is.Null);
            StageBBlock source33 = refund.Continuation.SourceBlocks.Single(static block => block.Ordinal == 33);
            Assert.That(source33.Kind, Is.EqualTo("Block"));
            Assert.That(source33.Reachable, Is.True);
            Assert.That(source33.ConditionKind, Is.EqualTo("WhenFalse"));
            Assert.That(source33.Operations, Is.Empty);
            Assert.That(source33.ContainsExcludedOperations, Is.False);
            Assert.That(source33.BranchValue, Is.Not.Null);
            Assert.That(source33.BranchValue!.Kind, Is.EqualTo(StageBOperationKind.LocalReference));
            Assert.That(source33.BranchValue.Type, Is.EqualTo("bool"));
            Assert.That(source33.BranchValue.Symbol, Does.Contain("::local:newAccountOutOfGas@"));
            Assert.That(source33.BranchValue.Operator, Is.EqualTo("declaration=False"));
            Assert.That(source33.BranchValue.Implicit, Is.False);
            Assert.That(source33.BranchValue.Children, Is.Empty);
            Assert.That(source33.FallThrough, Is.Not.Null);
            Assert.That(source33.FallThrough!.Destination, Is.EqualTo(34));
            Assert.That(source33.FallThrough.Semantics, Is.EqualTo("Regular"));
            Assert.That(source33.FallThrough.LeavingRegions, Is.Empty);
            Assert.That(source33.FallThrough.EnteringRegions, Is.Empty);
            Assert.That(source33.FallThrough.FinallyRegions, Is.Empty);
            Assert.That(source33.Conditional, Is.Not.Null);
            Assert.That(source33.Conditional!.Destination, Is.EqualTo(35));
            Assert.That(source33.Conditional.Semantics, Is.EqualTo("Regular"));
            Assert.That(source33.Conditional.LeavingRegions, Is.Empty);
            Assert.That(source33.Conditional.EnteringRegions, Is.Empty);
            Assert.That(source33.Conditional.FinallyRegions, Is.Empty);
            foreach ((int ordinal, string name, string constant) in new[]
            {
                (34, "Failure", "System.Byte:0"),
                (35, "Success", "System.Byte:1"),
            })
            {
                StageBBlock status = refund.Continuation.SourceBlocks.Single(block => block.Ordinal == ordinal);
                StageBTerm capture = status.Operations.Single();
                StageBTerm value = capture.Children.Single();
                string symbol = "byte global::Nethermind.Evm.StatusCode." + name;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That((status.Kind, status.Reachable, status.ConditionKind),
                        Is.EqualTo(("Block", true, "None")));
                    Assert.That(status.BranchValue, Is.Null);
                    Assert.That(status.ContainsExcludedOperations, Is.False);
                    Assert.That(status.FallThrough, Is.Not.Null);
                    Assert.That((status.FallThrough!.Destination, status.FallThrough.Semantics),
                        Is.EqualTo((36, "Regular")));
                    Assert.That(status.FallThrough.LeavingRegions, Is.Empty);
                    Assert.That(status.FallThrough.EnteringRegions, Is.Empty);
                    Assert.That(status.FallThrough.FinallyRegions, Is.Empty);
                    Assert.That(status.Conditional, Is.Null);
                    Assert.That((capture.Kind, capture.Type, capture.Symbol, capture.Operator, capture.Constant,
                        capture.Implicit, capture.ArgumentKind, capture.ParameterOrdinal, capture.RefKind, capture.Conversion),
                        Is.EqualTo((StageBOperationKind.FlowCapture, "", "", "capture=10", "", true, "", -1, "", "")));
                    Assert.That(capture.Binding, Is.Not.Null);
                    Assert.That((capture.Binding!.Capture, capture.Binding.RefKind, capture.Binding.IsRef,
                        capture.Binding.ArgumentMode),
                        Is.EqualTo((10, StageBRefKind.None, false, StageBArgumentMode.Value)));
                    Assert.That(capture.Binding.Call, Is.Null);
                    Assert.That(capture.Binding.AdditionalMembers, Is.Empty);
                    Assert.That((value.Kind, value.Type, value.Symbol, value.Operator, value.Constant,
                        value.Implicit, value.ArgumentKind, value.ParameterOrdinal, value.RefKind, value.Conversion),
                        Is.EqualTo((StageBOperationKind.FieldReference, "byte", symbol, "declaration=False", constant,
                            false, "", -1, "", "")));
                    Assert.That(value.Binding, Is.Not.Null);
                    Assert.That((value.Binding!.Capture, value.Binding.RefKind, value.Binding.IsRef,
                        value.Binding.ArgumentMode),
                        Is.EqualTo((-1, StageBRefKind.None, false, StageBArgumentMode.Value)));
                    Assert.That(value.Binding.Call, Is.Not.Null);
                    Assert.That((value.Binding.Call!.Target, value.Binding.Call.ReceiverChild),
                        Is.EqualTo((symbol, -1)));
                    Assert.That(value.Binding.Call.ArgumentChildren, Is.Empty);
                    Assert.That(value.Binding.AdditionalMembers, Is.Empty);
                    Assert.That(value.Children, Is.Empty);
                }
            }
            StageBBlock source36 = refund.Continuation.SourceBlocks.Single(static block => block.Ordinal == 36);
            StageBTerm statusAssignment = source36.Operations.Single();
            StageBTerm statusTarget = statusAssignment.Children[0];
            StageBTerm statusConversion = statusAssignment.Children[1];
            StageBTerm statusCapture = statusConversion.Children.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That((source36.Kind, source36.Reachable, source36.ConditionKind),
                    Is.EqualTo(("Block", true, "None")));
                Assert.That(source36.BranchValue, Is.Null);
                Assert.That(source36.ContainsExcludedOperations, Is.False);
                Assert.That(source36.FallThrough, Is.Not.Null);
                Assert.That((source36.FallThrough!.Destination, source36.FallThrough.Semantics),
                    Is.EqualTo((37, "Regular")));
                Assert.That(source36.FallThrough.LeavingRegions, Is.EqualTo(new[] { 5 }));
                Assert.That(source36.FallThrough.EnteringRegions, Is.Empty);
                Assert.That(source36.FallThrough.FinallyRegions, Is.Empty);
                Assert.That(source36.Conditional, Is.Null);
                Assert.That((statusAssignment.Kind, statusAssignment.Type, statusAssignment.Symbol,
                    statusAssignment.Operator, statusAssignment.Implicit),
                    Is.EqualTo((StageBOperationKind.SimpleAssignment, "int", "", "ref=False", true)));
                Assert.That((statusTarget.Kind, statusTarget.Type, statusTarget.Operator, statusTarget.Implicit),
                    Is.EqualTo((StageBOperationKind.LocalReference, "int", "declaration=True", true)));
                Assert.That(statusTarget.Symbol, Does.Contain("::local:statusCode@25802:None:int"));
                Assert.That(statusTarget.Children, Is.Empty);
                Assert.That((statusConversion.Kind, statusConversion.Type, statusConversion.Operator,
                    statusConversion.Implicit, statusConversion.Conversion),
                    Is.EqualTo((StageBOperationKind.Conversion, "int", "checked=False;tryCast=False", true,
                        "exists=True;identity=False;implicit=True;numeric=True;reference=False;nullable=False;user=False;union=False;method=;constrained=")));
                Assert.That((statusCapture.Kind, statusCapture.Type, statusCapture.Operator, statusCapture.Implicit),
                    Is.EqualTo((StageBOperationKind.FlowCaptureReference, "byte", "capture=10;initialization=False", true)));
                Assert.That(statusCapture.Binding, Is.Not.Null);
                Assert.That((statusCapture.Binding!.Capture, statusCapture.Binding.ArgumentMode),
                    Is.EqualTo((10, StageBArgumentMode.Value)));
                Assert.That(statusCapture.Children, Is.Empty);
            }
            StageBPrefixFunction simpleTransfer = prefix.Functions.Single(function =>
                function.Signature.Symbol == refund.Continuation.Function);
            StageBRegion postRefundRegion = simpleTransfer.Regions.Single(static region => region.Id == 5);
            Assert.That((postRefundRegion.Parent, postRefundRegion.Kind, postRefundRegion.FirstBlock,
                postRefundRegion.LastBlock, postRefundRegion.ExceptionType),
                Is.EqualTo((1, "LocalLifetime", 33, 36, "")));
            Assert.That(postRefundRegion.Locals, Is.Empty);
            Assert.That(postRefundRegion.Captures, Is.EqualTo(new[] { 10 }));
            Assert.That(simpleTransfer.Captures.Single(static capture => capture.Id == 10),
                Is.EqualTo(new StageBCapture(10, 5, "byte")));
            Assert.That(simpleTransfer.CaptureModes.Single(static capture => capture.Capture == 10).Mode,
                Is.EqualTo(StageBPrefixOperandMode.Value));
            Assert.That(simpleTransfer.Blocks.Single(static block => block.Ordinal == 32).Exit,
                Is.EqualTo(StageBPrefixExit.Suspend));
            Assert.That(simpleTransfer.Blocks.Select(static block => block.Ordinal), Does.Not.Contain(33));
            StageBPrefixFunction substate = prefix.Functions.Single(static function => function.Signature.Kind == StageBMemberKind.Constructor &&
                function.Signature.DeclaringType == "global::Nethermind.Evm.TransactionSubstate");
            StageBPrefixNode[] propertyWrites = PrefixNodes(substate.Blocks.SelectMany(static block => block.Operations))
                .Where(static node => node.Kind == StageBOperationKind.PropertyReference && node.Mode == StageBPrefixOperandMode.Location).ToArray();
            Assert.That(propertyWrites, Is.Not.Empty);
            Assert.That(propertyWrites.All(static node => node.Children[0].Mode == StageBPrefixOperandMode.Location), Is.True);
            foreach (StageBPrefixFunction function in prefix.Functions)
            {
                Assert.That(function.BlockBounds.Single(bound => bound.Block == function.Entry.Block).Fuel, Is.EqualTo(function.FuelBound));
                foreach (string callee in function.Calls)
                    Assert.That(Array.IndexOf(prefix.CalleeBeforeCaller, callee), Is.LessThan(Array.IndexOf(prefix.CalleeBeforeCaller, function.Signature.Symbol)));
            }
        }
    }

    [Test]
    public void Stage_b_prefix_post_refund_access_guard_retains_exact_source_shape()
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPrefixContinuation continuation = prefix.Refund.Continuation;
        StageBPrefixFunction function = prefix.Functions.Single(function => function.Signature.Symbol == continuation.Function);
        StageBBlock block = continuation.SourceBlocks.Single(static block => block.Ordinal == 37);
        Assert.That(block.BranchValue, Is.Not.Null);
        StageBTerm property = block.BranchValue!;
        Assert.That(property.Children, Has.Length.EqualTo(1));
        StageBTerm receiver = property.Children[0];
        Assert.That(property.Binding?.Call, Is.Not.Null);
        Assert.That(receiver.Binding, Is.Not.Null);
        Assert.That(block.FallThrough, Is.Not.Null);
        Assert.That(block.Conditional, Is.Not.Null);
        const string symbol = "bool global::Nethermind.Evm.Tracing.ITxTracer.IsTracingAccess";
        using (Assert.EnterMultipleScope())
        {
            Assert.That((block.Kind, block.Reachable, block.ConditionKind, block.ContainsExcludedOperations),
                Is.EqualTo(("Block", true, "WhenFalse", false)));
            Assert.That(block.Operations, Is.Empty);
            Assert.That((property.Kind, property.Type, property.Symbol, property.Operator, property.Constant,
                property.Implicit, property.ArgumentKind, property.ParameterOrdinal, property.RefKind, property.Conversion),
                Is.EqualTo((StageBOperationKind.PropertyReference, "bool", symbol, "", "", false, "", -1, "", "")));
            Assert.That((property.Binding!.Capture, property.Binding.RefKind, property.Binding.IsRef, property.Binding.ArgumentMode),
                Is.EqualTo((-1, StageBRefKind.None, false, StageBArgumentMode.Value)));
            Assert.That((property.Binding.Call!.Target, property.Binding.Call.ReceiverChild), Is.EqualTo((symbol, 0)));
            Assert.That(property.Binding.Call.ArgumentChildren, Is.Empty);
            Assert.That(property.Binding.AdditionalMembers, Is.Empty);
            Assert.That((receiver.Kind, receiver.Type, receiver.Symbol, receiver.Operator, receiver.Constant,
                receiver.Implicit, receiver.ArgumentKind, receiver.ParameterOrdinal, receiver.RefKind, receiver.Conversion),
                Is.EqualTo((StageBOperationKind.ParameterReference, "global::Nethermind.Evm.Tracing.ITxTracer",
                    function.Signature.Parameters.Single(static parameter => parameter.Ordinal == 3).Symbol,
                    "", "", false, "", -1, "", "")));
            Assert.That((receiver.Binding!.Capture, receiver.Binding.RefKind, receiver.Binding.IsRef, receiver.Binding.ArgumentMode),
                Is.EqualTo((-1, StageBRefKind.None, false, StageBArgumentMode.Value)));
            Assert.That(receiver.Binding.Call, Is.Null);
            Assert.That(receiver.Binding.AdditionalMembers, Is.Empty);
            Assert.That(receiver.Children, Is.Empty);
            foreach ((StageBEdge edge, int destination) in new[] { (block.FallThrough!, 38), (block.Conditional!, 39) })
            {
                Assert.That((edge.Destination, edge.Semantics), Is.EqualTo((destination, "Regular")));
                Assert.That(edge.LeavingRegions, Is.Empty);
                Assert.That(edge.EnteringRegions, Is.Empty);
                Assert.That(edge.FinallyRegions, Is.Empty);
            }
            Assert.That(function.Blocks.Select(static block => block.Ordinal), Does.Not.Contain(37));
        }
    }

    [Test]
    public void Stage_b_prefix_post_refund_call_frontier_retains_exact_source_shape([Values(38, 39)] int ordinal)
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPrefixContinuation continuation = prefix.Refund.Continuation;
        StageBPrefixFunction function = prefix.Functions.Single(function => function.Signature.Symbol == continuation.Function);
        StageBBlock block = continuation.SourceBlocks.Single(block => block.Ordinal == ordinal);
        StageBBlock expected = ExpectedPostRefundCallBlock(function, ordinal);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(block), JsonSerializer.SerializeToNode(expected)),
                Is.True, "Exact independently expected block " + ordinal);
            Assert.That(function.Blocks.Select(static block => block.Ordinal), Does.Not.Contain(ordinal));
        }
    }

    private static StageBBlock ExpectedPostRefundCallBlock(StageBPrefixFunction function, int ordinal)
    {
        const string processor = "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>";
        const string transaction = "global::Nethermind.Core.Transaction";
        const string header = "global::Nethermind.Core.BlockHeader";
        const string spec = "global::Nethermind.Core.Specs.IReleaseSpec";
        const string tracer = "global::Nethermind.Evm.Tracing.ITxTracer";
        const string options = "global::Nethermind.Evm.TransactionProcessing.ExecutionOptions";
        const string address = "global::Nethermind.Core.Address";
        const string substate = "global::Nethermind.Evm.TransactionSubstate";
        const string spentGas = "global::Nethermind.Evm.TransactionProcessing.GasConsumed";
        const string uint256 = "global::Nethermind.Int256.UInt256";
        const string result = "global::Nethermind.Evm.TransactionProcessing.TransactionResult";
        const string identity = "exists=True;identity=True;implicit=True;numeric=False;reference=False;nullable=False;user=False;union=False;method=;constrained=";
        StageBTermBinding plain = new(-1, StageBRefKind.None, false, StageBArgumentMode.Value, null, []);

        StageBTerm Parameter(int index)
        {
            StageBParameter parameter = function.Signature.Parameters[index];
            return new(StageBOperationKind.ParameterReference, parameter.Type, parameter.Symbol, "", "", false, "", -1, "", "", [])
            {
                Binding = plain with { RefKind = parameter.RefKind },
            };
        }

        StageBTerm Local(string name, string type) =>
            new(StageBOperationKind.LocalReference, type, function.Signature.Symbol + "::local:" + name + ":None:" + type,
                "declaration=False", "", false, "", -1, "", "", []) { Binding = plain };

        StageBTerm Argument(StageBTerm value, int index, bool readOnly) =>
            new(StageBOperationKind.Argument, "", "", "", "", false, "Explicit", index,
                readOnly ? "In" : "None", "in:" + identity + ";out:" + identity, [value])
            {
                Binding = plain with
                {
                    RefKind = readOnly ? StageBRefKind.In : StageBRefKind.None,
                    ArgumentMode = readOnly ? StageBArgumentMode.ReadOnlyLocation : StageBArgumentMode.Value,
                },
            };

        StageBTerm Call(string type, string signature, StageBTerm[] operands, int[] readOnlyArguments)
        {
            StageBTerm receiver = new(StageBOperationKind.InstanceReference, processor, "", "ContainingTypeInstance", "",
                true, "", -1, "", "", []) { Binding = plain };
            StageBTerm[] children = new StageBTerm[operands.Length + 1];
            children[0] = receiver;
            for (int index = 0; index < operands.Length; index++)
                children[index + 1] = Argument(operands[index], index, readOnlyArguments.Contains(index));
            return new(StageBOperationKind.Invocation, type, signature, "virtual=False", "", false, "", -1, "", "", children)
            {
                Binding = plain with { Call = new(signature, 0, Enumerable.Range(1, operands.Length).ToArray()) },
            };
        }

        StageBTerm Statement(StageBTerm call) =>
            new(StageBOperationKind.ExpressionStatement, "", "", "", "", false, "", -1, "", "", [call]) { Binding = plain };

        if (ordinal == 38)
        {
            string signature = $"void {processor}.ReportSimpleTransferAccess({transaction} tx, {spec} spec, {tracer} tracer, {address} recipient)";
            StageBTerm call = Call("void", signature, [Parameter(0), Parameter(2), Parameter(3), Parameter(8)], []);
            return new(38, "Block", true, "None", [Statement(call)], null, false, new(39, "Regular", [], [], []), null);
        }

        if (ordinal != 39) throw new AssertionException("Unexpected post-refund block: " + ordinal);
        string feeSignature = $"void {processor}.UpdateHeaderGasUsedAndPayFees({transaction} tx, {header} header, {spec} spec, {tracer} tracer, {options} opts, in {substate} substate, in {spentGas} spentGas, in {uint256} premiumPerGas, in {uint256} effectiveGasPrice, in {uint256} blobBaseFee, int statusCode)";
        StageBTerm feeCall = Call("void", feeSignature,
            [Parameter(0), Parameter(1), Parameter(2), Parameter(3), Parameter(4), Local("substate@24377", substate),
                Local("spentGas@25613", spentGas), Parameter(12), Parameter(11), Parameter(14), Local("statusCode@25802", "int")],
            [5, 6, 7, 8, 9]);
        string finalizeSignature = $"{result} {processor}.FinalizeTransaction({transaction} tx, {spec} spec, {tracer} tracer, {options} opts, bool restore, bool commit, bool deleteCallerAccount, in {uint256} senderReservedGasPayment, {address} executingAccount, in {substate} substate, {spentGas} spentGas, int statusCode)";
        StageBTerm finalizeCall = Call(result, finalizeSignature,
            [Parameter(0), Parameter(2), Parameter(3), Parameter(4), Parameter(5), Parameter(6), Parameter(7), Parameter(13),
                Parameter(8), Local("substate@24377", substate), Local("spentGas@25613", spentGas), Local("statusCode@25802", "int")],
            [7, 9]);
        return new(39, "Block", true, "None", [Statement(feeCall)], finalizeCall, false, new(40, "Return", [1], [], []), null);
    }

    [Test]
    public void Stage_b_prefix_fee_helper_entry_retains_source_signature_and_empty_entry()
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBInertFunction helper = prefix.PostRefundFeeHelper;
        StageBBlock entry = helper.SourceBlocks.Single(block => block.Ordinal == helper.Entry.Block);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(helper.Signature),
                JsonSerializer.SerializeToNode(ExpectedFeeHelperSignature(prefix))), Is.True);
            Assert.That(helper.Entry, Is.EqualTo(new StageBCfgPoint(0, 0)));
            Assert.That(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(entry), JsonSerializer.SerializeToNode(
                new StageBBlock(0, "Entry", true, "None", [], null, false, new(1, "Regular", [], [], []), null))), Is.True);
            Assert.That(prefix.Functions.Any(function => function.Signature.Symbol == helper.Signature.Symbol), Is.False);
            Assert.That(helper.SourceBlocks.Single(static block => block.Ordinal == 1).BranchValue?.Symbol,
                Does.Contain("SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters"));
        }
    }

    private static StageBMember ExpectedFeeHelperSignature(StageBPrefixProgram prefix)
    {
        StageBPrefixFunction caller = prefix.Functions.Single(function => function.Signature.Symbol == prefix.Refund.Continuation.Function);
        string symbol = ExpectedPostRefundCallBlock(caller, 39).Operations[0].Children[0].Symbol;
        (string Name, string Type, StageBRefKind Kind)[] parameters =
        [
            ("tx", "global::Nethermind.Core.Transaction", StageBRefKind.None),
            ("header", "global::Nethermind.Core.BlockHeader", StageBRefKind.None),
            ("spec", "global::Nethermind.Core.Specs.IReleaseSpec", StageBRefKind.None),
            ("tracer", "global::Nethermind.Evm.Tracing.ITxTracer", StageBRefKind.None),
            ("opts", "global::Nethermind.Evm.TransactionProcessing.ExecutionOptions", StageBRefKind.None),
            ("substate", "global::Nethermind.Evm.TransactionSubstate", StageBRefKind.In),
            ("spentGas", "global::Nethermind.Evm.TransactionProcessing.GasConsumed", StageBRefKind.In),
            ("premiumPerGas", "global::Nethermind.Int256.UInt256", StageBRefKind.In),
            ("effectiveGasPrice", "global::Nethermind.Int256.UInt256", StageBRefKind.In),
            ("blobBaseFee", "global::Nethermind.Int256.UInt256", StageBRefKind.In),
            ("statusCode", "int", StageBRefKind.None),
        ];
        return new(symbol, symbol, "UpdateHeaderGasUsedAndPayFees",
            "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>",
            StageBMemberKind.Method, StageBReceiverKind.Reference, "void", StageBRefKind.None,
            parameters.Select((parameter, index) => new StageBParameter(
                $"{symbol}::parameter:{index}:{parameter.Name}:{parameter.Kind}:{parameter.Type}",
                parameter.Name, index, parameter.Type, parameter.Kind, false)).ToArray());
    }

    [TestCase("order", "in UInt256 premiumPerGas,\n            in UInt256 effectiveGasPrice", "in UInt256 effectiveGasPrice,\n            in UInt256 premiumPerGas")]
    [TestCase("readonly", "in UInt256 premiumPerGas,", "UInt256 premiumPerGas,")]
    [TestCase("optional", "int statusCode)", "int statusCode = 1)")]
    [TestCase("guard", "if (SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters(opts, _parallel))", "if (SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters(opts, !_parallel))")]
    public void Stage_b_prefix_fee_helper_entry_rejects_retained_source_mutations(string mutation, string original, string replacement)
    {
        using SourceFixture fixture = new();
        string source = File.ReadAllText(Path.Combine(fixture.Root, Extractor.TransactionProcessorPath));
        int start = source.IndexOf("        private void UpdateHeaderGasUsedAndPayFees(", StringComparison.Ordinal);
        int end = source.IndexOf("        [SkipLocalsInit]", start, StringComparison.Ordinal);
        string method = source[start..end];
        Assert.That(method, Does.Contain(original));
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, method, method.Replace(original, replacement, StringComparison.Ordinal));
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing, mutation);
        if (mutation != "guard")
        {
            Assert.That(() => fixture.RequireStageBPlan(), Throws.TypeOf<ExtractionException>()
                .With.Message.StartsWith("Stage-B callee:"), mutation);
            return;
        }
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        Assert.That(() => StageBArtifact.ValidateProgram(prefix), Throws.TypeOf<ExtractionException>(), mutation);
        Assert.That(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(prefix.PostRefundFeeHelper.Signature),
            JsonSerializer.SerializeToNode(ExpectedFeeHelperSignature(prefix))), Is.True);
    }

    [Test]
    public void Stage_b_prefix_fee_helper_entry_rejects_resigned_inert_metadata_changes([Range(0, 4)] int mutation)
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBInertFunction helper = prefix.PostRefundFeeHelper;
        StageBInertFunction changed = mutation switch
        {
            0 => helper with { Signature = helper.Signature with { Receiver = StageBReceiverKind.Static } },
            1 => helper with { Signature = helper.Signature with { Parameters = helper.Signature.Parameters.Reverse().ToArray() } },
            2 => helper with { Entry = new(1, 0) },
            3 => helper with { Entry = new(0, 1) },
            _ => helper with { SourceBlocks = helper.SourceBlocks.Skip(1).ToArray() },
        };
        StageBPrefixProgram candidate = prefix with { PostRefundFeeHelper = changed, Integrity = "" };
        candidate = candidate with { Integrity = StageBPrefixIntegrity.Compute(candidate) };
        Assert.That(() => StageBArtifact.ValidateProgram(candidate), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Stage_b_prefix_access_projection_is_independent_of_actions([Values] bool access, [Values] bool actions)
    {
        StageBPlan plan = StageBLowering.Build(FindRepoRoot());
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(plan);
        StageBPrefixFunction root = prefix.Functions.Single(function => function.Signature.Symbol == prefix.Entry);
        StageBPrefixNode property = PrefixNodes(prefix).First(static node =>
            node.Kind == StageBOperationKind.PropertyReference && node.Call?.Target.Member.Name == "IsTracingActions");
        StageBMember member = plan.Members.Single(static member =>
            member.Symbol == "bool global::Nethermind.Evm.Tracing.ITxTracer.IsTracingAccess");
        StageBPrefixNode receiver = property.Children.Single() with
        {
            Symbol = root.Signature.Parameters.Single(static parameter => parameter.Name == "tracer").Symbol,
        };
        property = property with
        {
            Symbol = member.Symbol, Children = [receiver],
            Binding = property.Binding with { Call = property.Binding.Call! with { Target = member.Symbol } },
            Call = property.Call! with { Target = property.Call.Target with { Member = member } },
        };
        StageBPrefixBlock entry = root.Blocks.Single(block => block.Ordinal == root.Entry.Block) with
        {
            Operations = [property], BranchValue = null, ConditionKind = "None", Exit = StageBPrefixExit.Return,
            FallThrough = null, Conditional = null,
        };
        StageBPrefixFunction changedRoot = root with { Blocks = [entry] };
        StageBPrefixProgram changed = prefix with
        {
            Functions = prefix.Functions.Select(function => function.Signature.Symbol == prefix.Entry ? changedRoot : function).ToArray(),
        };
        changed = changed with { Integrity = StageBPrefixIntegrity.Compute(changed) };
        StageBPostNonceInput input = InterpreterInput(0) with { Tracer = new(IsTracingActions: actions, IsTracingAccess: access) };
        StageBResponseTape tape = new();

        StageBPrefixRun run = StageBPrefixInterpreter.Run(changed, input, tape, changed.FuelBound);

        Assert.That(run.ReturnValue, Is.Not.Null, run.Diagnostic);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.Returned), run.Diagnostic);
            Assert.That(run.ReturnValue!.Kind, Is.EqualTo(StageBValueKind.Bool));
            Assert.That(run.ReturnValue.Boolean, Is.EqualTo(access));
            Assert.That(run.RemainingFuel, Is.EqualTo(changed.FuelBound - 3));
            Assert.That(run.Requests, Is.Empty);
            Assert.That(tape.Consumed, Is.Zero);
        }
    }

    [Test]
    public void Stage_b_fuel_bounds_include_cold_static_initializers()
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPrefixFunction root = prefix.Functions.Single(function => function.Signature.Symbol == prefix.Entry);
        StageBPrefixFunction calculate = prefix.Functions.Single(static function => function.Signature.Name == "CalculateAvailableGas");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(prefix.FuelBound, Is.EqualTo(root.FuelBound));
            Assert.That(prefix.FuelBound, Is.EqualTo(853));
            Assert.That(calculate.FuelBound, Is.EqualTo(106));
            Assert.That(calculate.BlockBounds.Select(static bound => (bound.Block, bound.Fuel)),
                Is.EqualTo(new[] { (0, 106L), (1, 105L), (2, 8L), (3, 33L), (4, 3L), (5, 1L) }));
        }
    }

    [TestCase(0L, false)]
    [TestCase(7L, true)]
    public void Stage_b_prefix_interpreter_reaches_exact_refund_boundary(long transferredValue, bool subtracts)
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPostNonceInput input = InterpreterInput(transferredValue);
        List<StageBExchange> exchanges =
        [
            new StageBCodeLookupExchange(new StageBCodeLookupRequest("recipient", FollowDelegation: true), IsEmpty: true, DelegationAddress: null),
            new StageBUnitExchange(new StageBTraceRequest("IncrementEmptyCalls", [])),
        ];
        if (subtracts) exchanges.Add(new StageBUnitExchange(new StageBSubtractBalanceRequest("sender", transferredValue)));
        exchanges.Add(new StageBBoolExchange(new StageBAddBalanceRequest("recipient", transferredValue), true));
        StageBResponseTape tape = new(exchanges.ToArray());

        StageBPrefixRun run = StageBPrefixInterpreter.Run(prefix, input, tape, prefix.FuelBound);

        Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.RefundSuspended), run.Diagnostic);
        Assert.That(tape.Remaining, Is.Zero);
        StageBRefundSuspension suspension = run.Suspension!;
        Assert.That(suspension.RemainingFuel, Is.EqualTo(transferredValue == 0 ? 359 : 309));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(suspension.Operands.Select(static operand => operand.Ordinal), Is.EqualTo(Enumerable.Range(0, 12)));
            Assert.That(suspension.GasAvailable, Is.EqualTo(new StageBGasValue(79_000, 0, 0, 0, 0)));
            Assert.That(suspension.PostIntrinsicStateReservoir, Is.Zero);
            Assert.That(suspension.Operands[4].Location, Does.Contain("::local:substate@"));
            Assert.That(suspension.Operands[5].Location, Does.Contain("::parameter:10:gasAvailable:"));
            Assert.That(suspension.Operands[6].Location, Does.Contain("::local:opcodeGasPrice@"));
            Assert.That(suspension.Operands[8].Location, Does.Contain("::local:floorGas@"));
            Assert.That(suspension.Operands[9].Location, Does.Contain("::local:standardGas@"));
            Assert.That(suspension.Operands[10].Location, Is.Empty);
            Assert.That(suspension.Operands[11].Value, Is.EqualTo(StageBValue.Bool(false)));
            Assert.That(run.Requests.OfType<StageBTraceRequest>().Select(static request => request.Operation),
                Is.EqualTo(new[] { "IncrementEmptyCalls" }));
        }
    }

    [Test]
    public void Stage_b_prefix_interpreter_executes_state_charge_before_refund()
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPostNonceInput baseline = InterpreterInput(7);
        StageBPostNonceInput input = baseline with
        {
            Spec = new(IsEip8037Enabled: true, IsEip7708Enabled: false),
            IntrinsicGas = new(new(21_000, 2, 2, 0, 0), new(21_000, 0, 0, 0, 0)),
            NewAccountStateCost = 183_600,
            Transaction = baseline.Transaction with { GasLimit = 300_000 },
        };
        StageBResponseTape tape = new(
            new StageBCodeLookupExchange(new StageBCodeLookupRequest("recipient", FollowDelegation: false), true, null),
            new StageBUnitExchange(new StageBTraceRequest("IncrementEmptyCalls", [])),
            new StageBBoolExchange(new StageBIsDeadAccountRequest("recipient"), true),
            new StageBUnitExchange(new StageBSubtractBalanceRequest("sender", 7)),
            new StageBBoolExchange(new StageBAddBalanceRequest("recipient", 7), true));

        StageBPrefixRun run = StageBPrefixInterpreter.Run(prefix, input, tape, prefix.FuelBound);

        Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.RefundSuspended), run.Diagnostic);
        Assert.That(run.Suspension!.RemainingFuel, Is.EqualTo(226));
        Assert.That(run.Suspension!.GasAvailable, Is.EqualTo(new StageBGasValue(95_398, 0, 183_602, 183_600, 0)));
        Assert.That(run.Suspension.PostIntrinsicStateReservoir, Is.Zero);
        Assert.That(tape.Remaining, Is.Zero);
    }

    [TestCase(0L, "recipient", 357)]
    [TestCase(7L, "recipient", 299)]
    [TestCase(7L, "sender", 393)]
    public void Stage_b_prefix_interpreter_executes_eip8037_zero_existing_and_self_transfers(
        long transferredValue, string recipient, int expectedFuel)
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPostNonceInput baseline = InterpreterInput(transferredValue);
        StageBPostNonceInput input = baseline with
        {
            Spec = new(IsEip8037Enabled: true, IsEip7708Enabled: false),
            Transaction = baseline.Transaction with { Recipient = recipient },
        };
        List<StageBExchange> exchanges =
        [
            new StageBCodeLookupExchange(new StageBCodeLookupRequest(recipient, FollowDelegation: false), true, null),
            new StageBUnitExchange(new StageBTraceRequest("IncrementEmptyCalls", [])),
        ];
        if (transferredValue != 0 && recipient != "sender")
        {
            exchanges.Add(new StageBBoolExchange(new StageBIsDeadAccountRequest(recipient), false));
            exchanges.Add(new StageBUnitExchange(new StageBSubtractBalanceRequest("sender", transferredValue)));
        }
        if (recipient != "sender")
            exchanges.Add(new StageBBoolExchange(new StageBAddBalanceRequest(recipient, transferredValue), true));
        StageBResponseTape tape = new(exchanges.ToArray());

        StageBPrefixRun run = StageBPrefixInterpreter.Run(prefix, input, tape, prefix.FuelBound);

        Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.RefundSuspended), run.Diagnostic);
        Assert.That(run.Suspension!.RemainingFuel, Is.EqualTo(expectedFuel));
        Assert.That(tape.Remaining, Is.Zero);
    }

    [TestCase(false, null)]
    [TestCase(true, "delegate")]
    public void Stage_b_prefix_interpreter_commits_before_leaving_selected_domain(bool isEmpty, string? delegation)
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPostNonceInput baseline = InterpreterInput(7);
        StageBPostNonceInput input = baseline with { Spec = new(IsEip8037Enabled: true, IsEip7708Enabled: false) };
        StageBResponseTape tape = new(
            new StageBCodeLookupExchange(new StageBCodeLookupRequest("recipient", FollowDelegation: false), isEmpty, delegation),
            new StageBUnitExchange(new StageBCommitRequest(TracingState: false, CommitRoots: false)));

        StageBPrefixRun run = StageBPrefixInterpreter.Run(prefix, input, tape, prefix.FuelBound);

        Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.OutsideSelectedDomain), run.Diagnostic);
        Assert.That(run.Requests, Is.EqualTo(new StageBRequest[]
        {
            new StageBCodeLookupRequest("recipient", FollowDelegation: false),
            new StageBCommitRequest(TracingState: false, CommitRoots: false),
        }));
        Assert.That(tape.Remaining, Is.Zero);
    }

    [Test]
    public void Stage_b_prefix_interpreter_refund_uses_post_charge_reservoir()
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPostNonceInput baseline = InterpreterInput(7);
        StageBPostNonceInput input = baseline with
        {
            Spec = new(IsEip8037Enabled: true, IsEip7708Enabled: false),
            Transaction = baseline.Transaction with { GasLimit = 17_277_216 },
            IntrinsicGas = new(new(21_000, 0, 0, 0, 0), new(21_000, 0, 0, 0, 0)),
        };
        StageBResponseTape tape = new(
            new StageBCodeLookupExchange(new StageBCodeLookupRequest("recipient", FollowDelegation: false), true, null),
            new StageBUnitExchange(new StageBTraceRequest("IncrementEmptyCalls", [])),
            new StageBBoolExchange(new StageBIsDeadAccountRequest("recipient"), true),
            new StageBUnitExchange(new StageBSubtractBalanceRequest("sender", 7)),
            new StageBBoolExchange(new StageBAddBalanceRequest("recipient", 7), true));

        StageBPrefixRun run = StageBPrefixInterpreter.Run(prefix, input, tape, prefix.FuelBound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.RefundSuspended), run.Diagnostic);
            Assert.That(run.Suspension!.RemainingFuel, Is.EqualTo(226));
            Assert.That(run.Suspension!.GasAvailable,
                Is.EqualTo(new StageBGasValue(16_756_216, 316_400, 183_600, 0, 0)));
            Assert.That(run.Suspension.PostIntrinsicStateReservoir, Is.EqualTo(316_400));
            Assert.That(run.Suspension.Operands[10].Value, Is.EqualTo(StageBValue.Int64(316_400)));
            Assert.That(tape.Remaining, Is.Zero);
        }
    }

    [Test]
    public void Stage_b_prefix_interpreter_wrong_reply_type_is_not_consumed_or_recorded()
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBCodeLookupRequest request = new("recipient", FollowDelegation: true);
        StageBResponseTape tape = new(new StageBBoolExchange(request, true));

        StageBPrefixRun run = StageBPrefixInterpreter.Run(prefix, InterpreterInput(0), tape, prefix.FuelBound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.Rejected));
            Assert.That(run.Diagnostic, Does.Contain("response-tape"));
            Assert.That(run.Requests, Is.Empty);
            Assert.That(tape.Consumed, Is.Zero);
        }
    }

    [Test]
    public void Stage_b_prefix_interpreter_preserves_failed_state_charge_until_execution_gas_clear()
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPostNonceInput input = InterpreterInput(7) with
        {
            Spec = new(IsEip8037Enabled: true, IsEip7708Enabled: false),
            IntrinsicGas = new(new(21_000, 2, 2, 0, 0), new(21_000, 0, 0, 0, 0)),
        };
        StageBResponseTape tape = new(
            new StageBCodeLookupExchange(new StageBCodeLookupRequest("recipient", FollowDelegation: false), true, null),
            new StageBUnitExchange(new StageBTraceRequest("IncrementEmptyCalls", [])),
            new StageBBoolExchange(new StageBIsDeadAccountRequest("recipient"), true));

        StageBPrefixRun run = StageBPrefixInterpreter.Run(prefix, input, tape, prefix.FuelBound);

        Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.RefundSuspended), run.Diagnostic);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Suspension!.RemainingFuel, Is.EqualTo(328));
            Assert.That(run.Suspension!.GasAvailable, Is.EqualTo(new StageBGasValue(0, 0, 2, 0, 0)));
            Assert.That(run.Suspension.Substate.Field("EvmExceptionType").Signed, Is.EqualTo(4));
            Assert.That(run.Requests.OfType<StageBSubtractBalanceRequest>(), Is.Empty);
            Assert.That(run.Requests.OfType<StageBAddBalanceRequest>(), Is.Empty);
            Assert.That(tape.Remaining, Is.Zero);
        }
    }

    [Test]
    public void Stage_b_prefix_interpreter_rejects_initialization_entry_domain_before_requests()
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPostNonceInput baseline = InterpreterInput(1);
        StageBPostNonceInput input = baseline with { Transaction = baseline.Transaction with { GasLimit = 20_999 } };
        StageBResponseTape tape = new();

        StageBPrefixRun run = StageBPrefixInterpreter.Run(prefix, input, tape, prefix.FuelBound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.Rejected));
            Assert.That(run.Diagnostic, Does.Contain("initialization entry-domain exclusion"));
            Assert.That(tape.Consumed, Is.Zero);
        }
    }

    [Test]
    public void Stage_b_prefix_interpreter_uses_source_lowered_exception_enum_value()
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst("src/Nethermind/Nethermind.Evm/EvmException.cs",
            "    StackUnderflow,\n    OutOfGas,", "    StackUnderflow,\n    Probe,\n    OutOfGas,");
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        StageBPostNonceInput input = InterpreterInput(7) with
        {
            Spec = new(IsEip8037Enabled: true, IsEip7708Enabled: false),
            IntrinsicGas = new(new(21_000, 2, 2, 0, 0), new(21_000, 0, 0, 0, 0)),
        };
        StageBResponseTape tape = new(
            new StageBCodeLookupExchange(new StageBCodeLookupRequest("recipient", FollowDelegation: false), true, null),
            new StageBUnitExchange(new StageBTraceRequest("IncrementEmptyCalls", [])),
            new StageBBoolExchange(new StageBIsDeadAccountRequest("recipient"), true));

        StageBPrefixRun run = StageBPrefixInterpreter.Run(prefix, input, tape, prefix.FuelBound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.RefundSuspended), run.Diagnostic);
            Assert.That(run.Suspension!.Substate.Field("EvmExceptionType").Signed, Is.EqualTo(5));
            Assert.That(tape.Remaining, Is.Zero);
        }
    }

    [TestCase("source")]
    [TestCase("theorem")]
    [TestCase("entry")]
    public void Stage_b_prefix_interpreter_rejects_linked_kernel_stage_drift_before_requests(string mutation)
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBStage[] stages = prefix.AcceptedStages.Select(stage => stage.Name == "state-charge"
            ? mutation switch
            {
                "source" => stage with { SourceClosure = stage.SourceClosure.Select((dependency, index) => index == 0
                    ? dependency with { SourceSha256 = new string('0', 64) } : dependency).ToArray() },
                "theorem" => stage with { Theorem = stage.Theorem + ".changed" },
                "entry" => stage with { EntryPoints = [stage.EntryPoints[0] + ".changed"] },
                _ => throw new AssertionException(mutation),
            }
            : stage).ToArray();
        StageBPrefixProgram changed = prefix with { AcceptedStages = stages, Integrity = "" };
        changed = changed with { Integrity = StageBPrefixIntegrity.Compute(changed) };
        StageBResponseTape tape = new();

        StageBPrefixRun run = StageBPrefixInterpreter.Run(changed, InterpreterInput(0), tape, changed.FuelBound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.Rejected));
            Assert.That(run.Diagnostic, Does.Contain("stage-source"));
            Assert.That(tape.Consumed, Is.Zero);
        }
    }

    [Test]
    public void Stage_b_prefix_finish_carries_last_result_across_empty_block([Values] bool hasBranchValue)
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPrefixFunction root = prefix.Functions.Single(function => function.Signature.Symbol == prefix.Entry);
        StageBPrefixBlock entry = root.Blocks.Single(block => block.Ordinal == root.Entry.Block);
        StageBPrefixNode literal = entry.Operations[0] with
        {
            Kind = StageBOperationKind.Literal, Type = "long", Constant = "System.Int64:71", Children = [], AdditionalTargets = [],
        };
        StageBPrefixBlock first = entry with
        {
            Ordinal = 11, Operations = [literal], BranchValue = hasBranchValue ? literal with { Constant = "System.Int64:19" } : null,
            Exit = StageBPrefixExit.Branch, ConditionKind = "None", FallThrough = new(37, "Return", [], [], []), Conditional = null,
        };
        StageBPrefixBlock last = first with { Ordinal = 37, Operations = [], BranchValue = null, Exit = StageBPrefixExit.Return, FallThrough = null };
        StageBPrefixFunction changedRoot = root with { Entry = root.Entry with { Block = 11 }, Blocks = [first, last] };
        StageBPrefixProgram changed = prefix with { Functions = prefix.Functions.Select(function => function.Signature.Symbol == prefix.Entry ? changedRoot : function).ToArray() };
        changed = changed with { Integrity = StageBPrefixIntegrity.Compute(changed) };
        StageBResponseTape tape = new();

        StageBPrefixRun run = StageBPrefixInterpreter.Run(changed, InterpreterInput(0), tape, changed.FuelBound);

        Assert.That(run.ReturnValue, Is.Not.Null, run.Diagnostic);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.Returned), run.Diagnostic);
            Assert.That(run.ReturnValue!.Kind, Is.EqualTo(StageBValueKind.Int64));
            Assert.That(run.ReturnValue.Signed, Is.EqualTo(hasBranchValue ? 19 : 71));
            Assert.That(run.RemainingFuel, Is.EqualTo(changed.FuelBound - (hasBranchValue ? 4 : 3)));
            Assert.That(run.Requests, Is.Empty);
            Assert.That(tape.Consumed, Is.Zero);
        }
    }

    [Test]
    public void Stage_b_prefix_finish_keeps_outside_before_poison_evaluation([Values] bool outside)
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPrefixFunction root = prefix.Functions.Single(function => function.Signature.Symbol == prefix.Entry);
        StageBPrefixBlock entry = root.Blocks.Single(block => block.Ordinal == root.Entry.Block);
        StageBPrefixNode poison = entry.Operations[0] with { Kind = StageBOperationKind.Parenthesized, Children = [], AdditionalTargets = [] };
        StageBPrefixBlock changedBlock = entry with
        {
            Operations = [poison], BranchValue = poison,
            Exit = outside ? StageBPrefixExit.OutsideSelectedDomain : StageBPrefixExit.Branch,
        };
        StageBPrefixFunction changedRoot = root with { Blocks = root.Blocks.Select(block => block.Ordinal == entry.Ordinal ? changedBlock : block).ToArray() };
        StageBPrefixProgram changed = prefix with { Functions = prefix.Functions.Select(function => function.Signature.Symbol == prefix.Entry ? changedRoot : function).ToArray() };
        changed = changed with { Integrity = StageBPrefixIntegrity.Compute(changed) };
        StageBResponseTape tape = new();

        StageBPrefixRun run = StageBPrefixInterpreter.Run(changed, InterpreterInput(0), tape, changed.FuelBound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(outside ? StageBRunOutcomeKind.OutsideSelectedDomain : StageBRunOutcomeKind.Rejected), run.Diagnostic);
            Assert.That(run.RemainingFuel, Is.EqualTo(changed.FuelBound - (outside ? 1 : 2)));
            if (!outside) Assert.That(run.Diagnostic, Does.Contain("transparent-arity"));
            Assert.That(run.Requests, Is.Empty);
            Assert.That(tape.Consumed, Is.Zero);
        }
    }

    [Test]
    public void Stage_b_prefix_rejects_return_nodes_before_execution(
        [Values("operation", "branch", "nested")] string placement, [Range(0, 2)] int childCount)
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPrefixFunction root = prefix.Functions.Single(function => function.Signature.Symbol == prefix.Entry);
        StageBPrefixBlock entry = root.Blocks.Single(block => block.Ordinal == root.Entry.Block);
        StageBPrefixNode literal = entry.Operations[0] with
        {
            Kind = StageBOperationKind.Literal, Type = "long", Constant = "System.Int64:71", Call = null,
            Children = [], AdditionalTargets = [],
        };
        StageBPrefixNode returnNode = literal with
        {
            Kind = StageBOperationKind.Return, Children = Enumerable.Repeat(literal, childCount).ToArray(),
        };
        StageBPrefixNode rootNode = placement == "nested" ? literal with
        {
            Kind = StageBOperationKind.ExpressionStatement, Children = [returnNode],
        } : returnNode;
        StageBPrefixBlock changedBlock = entry with
        {
            Operations = placement == "branch" ? [] : [rootNode],
            BranchValue = placement == "branch" ? rootNode : null,
        };
        StageBPrefixFunction changedRoot = root with
        {
            Blocks = root.Blocks.Select(block => block.Ordinal == entry.Ordinal ? changedBlock : block).ToArray(),
        };
        StageBPrefixProgram changed = prefix with
        {
            Functions = prefix.Functions.Select(function => function.Signature.Symbol == prefix.Entry ? changedRoot : function).ToArray(),
            Integrity = "",
        };
        changed = changed with { Integrity = StageBPrefixIntegrity.Compute(changed) };
        StageBResponseTape tape = new();

        StageBPrefixRun run = StageBPrefixInterpreter.Run(changed, InterpreterInput(0), tape, changed.FuelBound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.Rejected));
            Assert.That(run.Diagnostic, Does.Contain("unsupported-operation: Return"));
            Assert.That(run.RemainingFuel, Is.EqualTo(changed.FuelBound));
            Assert.That(run.Requests, Is.Empty);
            Assert.That(tape.Consumed, Is.Zero);
        }
    }

    [Test]
    public void Stage_b_prefix_local_return_projection_uses_constructor_receiver_or_callee_value(
        [Values] bool constructing, [Values(0, 1, 2)] int mode)
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPrefixFunction root = prefix.Functions.Single(function => function.Signature.Symbol == prefix.Entry);
        StageBPrefixNode creation = PrefixNodes(prefix).First(static node => node.Kind == StageBOperationKind.ObjectCreation &&
            node.Call?.Target is { Kind: StageBPrefixTargetKind.Local, Member.DeclaringType: "global::Nethermind.Evm.TransactionSubstate" });
        StageBPrefixFunction constructor = prefix.Functions.Single(function => function.Signature.Symbol == creation.Call!.Target.Body);
        StageBPrefixBlock rootEntry = root.Blocks.Single(block => block.Ordinal == root.Entry.Block);
        StageBPrefixNode literal = creation with
        {
            Kind = StageBOperationKind.Literal, Type = "long", Constant = "System.Int64:71", Children = [], AdditionalTargets = [], Call = null,
        };
        StageBPrefixBlock childEntry = rootEntry with
        {
            Ordinal = 41, Operations = [literal], BranchValue = null, Exit = StageBPrefixExit.Return,
            ConditionKind = "None", FallThrough = null, Conditional = null,
        };
        StageBPrefixFunction child = constructor with
        {
            Signature = constructor.Signature with { Parameters = [] }, Entry = new(41, 0), Blocks = [childEntry],
        };
        StageBPrefixNode invocation = creation with
        {
            Kind = constructing ? StageBOperationKind.ObjectCreation : StageBOperationKind.Invocation,
            Mode = (StageBPrefixOperandMode)mode, Children = [], AdditionalTargets = [],
            Call = creation.Call! with { Arguments = [], ReceiverChild = -1 },
        };
        StageBPrefixFunction changedRoot = root with { Blocks = [childEntry with { Ordinal = root.Entry.Block, Operations = [invocation] }] };
        StageBPrefixProgram changed = prefix with
        {
            Functions = prefix.Functions.Select(function => function.Signature.Symbol == prefix.Entry ? changedRoot :
                function.Signature.Symbol == child.Signature.Symbol ? child : function).ToArray(),
        };
        changed = changed with { Integrity = StageBPrefixIntegrity.Compute(changed) };
        StageBResponseTape tape = new();
        StageBPrefixRun run = StageBPrefixInterpreter.Run(changed, InterpreterInput(0), tape, changed.FuelBound);

        Assert.That(run.ReturnValue, Is.Not.Null, run.Diagnostic);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.Returned), run.Diagnostic);
            Assert.That(run.ReturnValue!.Kind, Is.EqualTo(constructing ? StageBValueKind.Struct : StageBValueKind.Int64));
            if (constructing) Assert.That(run.ReturnValue.Type, Is.EqualTo(creation.Call!.Target.Member.DeclaringType));
            else Assert.That(run.ReturnValue.Signed, Is.EqualTo(71));
            Assert.That(run.RemainingFuel, Is.EqualTo(changed.FuelBound - 4));
            Assert.That(run.Requests, Is.Empty);
            Assert.That(tape.Consumed, Is.Zero);
        }
    }

    [Test]
    public void Stage_b_prefix_interpreter_rejects_tampered_program_before_requests()
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPrefixFunction root = prefix.Functions.Single(function => function.Signature.Symbol == prefix.Entry);
        StageBPrefixBlock entry = root.Blocks.Single(block => block.Ordinal == root.Entry.Block);
        StageBPrefixBlock changedBlock = entry with { Operations = [entry.Operations[0] with { Kind = StageBOperationKind.Coalesce }] };
        StageBPrefixFunction changedRoot = root with { Blocks = root.Blocks.Select(block => block.Ordinal == entry.Ordinal ? changedBlock : block).ToArray() };
        StageBPrefixProgram changed = prefix with { Functions = prefix.Functions.Select(function => function.Signature.Symbol == prefix.Entry ? changedRoot : function).ToArray() };
        StageBResponseTape tape = new();

        StageBPrefixRun run = StageBPrefixInterpreter.Run(changed, InterpreterInput(0), tape, changed.FuelBound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.Rejected));
            Assert.That(run.Diagnostic, Does.Contain("program-integrity"));
            Assert.That(tape.Consumed, Is.Zero);
        }
    }

    [TestCase(true, false, "EIP-7708")]
    [TestCase(false, true, "warmup")]
    public void Stage_b_prefix_interpreter_rejects_unimplemented_input_routes_before_requests(bool eip7708, bool warmup, string diagnostic)
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPostNonceInput input = InterpreterInput(1) with
        {
            Spec = new(IsEip8037Enabled: false, IsEip7708Enabled: eip7708),
            Warmup = warmup,
        };
        StageBResponseTape tape = new();

        StageBPrefixRun run = StageBPrefixInterpreter.Run(prefix, input, tape, prefix.FuelBound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.Rejected));
            Assert.That(run.Diagnostic, Does.Contain(diagnostic));
            Assert.That(tape.Consumed, Is.Zero);
        }
    }

    [Test]
    public void Stage_b_prefix_interpreter_rejects_state_tracing_before_requests()
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPostNonceInput input = InterpreterInput(1) with { Tracer = new(IsTracingState: true) };
        StageBResponseTape tape = new();

        StageBPrefixRun run = StageBPrefixInterpreter.Run(prefix, input, tape, prefix.FuelBound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Kind, Is.EqualTo(StageBRunOutcomeKind.Rejected));
            Assert.That(run.Diagnostic, Does.Contain("state tracing"));
            Assert.That(tape.Consumed, Is.Zero);
        }
    }

    [Test]
    public void Stage_b_trace_tape_compares_nested_values_structurally()
    {
        StageBTraceRequest expected = new("ReportAction",
        [
            StageBValue.BytesValue([1, 2, 3]),
            StageBValue.Struct("payload", ("nested", StageBValue.BytesValue([4, 5]))),
        ]);
        StageBResponseTape tape = new(new StageBUnitExchange(expected));
        StageBTraceRequest observed = new("ReportAction",
        [
            StageBValue.BytesValue([1, 2, 3]),
            StageBValue.Struct("payload", ("nested", StageBValue.BytesValue([4, 5]))),
        ]);

        Assert.That(() => tape.Take<StageBUnitExchange>(observed), Throws.Nothing);
        Assert.That(tape.Remaining, Is.Zero);
    }

    private static StageBPostNonceInput InterpreterInput(long transferredValue) => new(
        new("sender", "recipient", transferredValue, [], GasLimit: 100_000),
        new(IsEip8037Enabled: false, IsEip7708Enabled: false),
        new(),
        new(new(21_000, 0, 0, 0, 0), new(21_000, 0, 0, 0, 0)),
        Restore: false,
        Commit: true,
        DeleteCallerAccount: false,
        Warmup: false,
        OpcodeGasPrice: 2,
        PremiumPerGas: 1,
        SenderReservedGasPayment: 100_000,
        BlobBaseFee: 0,
        ExecutionGasLimitCap: 16_777_216,
        NewAccountStateCost: 183_600);

    [Test]
    public void Stage_b_prefix_uses_cfg_not_statement_copies()
    {
        StageBPlan plan = StageBLowering.Build(FindRepoRoot());
        StageBPrefixProgram expected = StageBPrefixCompiler.Compile(plan);
        StageBPlan changed = plan with { Methods = plan.Methods.Select(static method => method with { Statements = [] }).ToArray() };
        Assert.That(JsonSerializer.Serialize(StageBPrefixCompiler.Compile(changed)), Is.EqualTo(JsonSerializer.Serialize(expected)));
    }

    [Test]
    public void Stage_b_prefix_keeps_post_nonce_effect_at_entry()
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, "Address? simpleTransferRecipient = PrepareSimpleTransferFastPath",
            "WorldState.Commit(spec, NullTxTracer.Instance, commitRoots: false);\n            Address? simpleTransferRecipient = PrepareSimpleTransferFastPath");
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        StageBPrefixFunction entry = prefix.Functions.Single(function => function.Signature.Symbol == prefix.Entry);
        StageBPrefixNode first = entry.Blocks.Single(block => block.Ordinal == entry.Entry.Block).Operations[0];
        Assert.That(PrefixNodes([first]).Single(static node => node.Kind == StageBOperationKind.Invocation).Call!.Target.Member.Name, Is.EqualTo("Commit"));
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void Stage_b_prefix_accepts_explicit_equivalent_refund_syntax(bool explicitReceiver, bool explicitDefault)
    {
        using SourceFixture fixture = new();
        string replacement = SimpleRefundCall;
        if (explicitReceiver) replacement = replacement.Replace("= Refund(", "= this.Refund(", StringComparison.Ordinal);
        if (explicitDefault) replacement = replacement.Replace("postIntrinsicStateReservoir);", "postIntrinsicStateReservoir, topLevelCreateStateGasCharged: false);", StringComparison.Ordinal);
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, SimpleRefundCall, replacement);
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPrefixSuspension boundary = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan()).Refund;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(boundary.OrderedOperands[0].Implicit, Is.EqualTo(!explicitReceiver));
            Assert.That(boundary.Call.Arguments[11].Implicit, Is.EqualTo(!explicitDefault));
            Assert.That(boundary.OrderedOperands[boundary.Call.Arguments[11].Child].Children[0].Constant, Is.EqualTo("bool:false"));
        }
    }

    [TestCase("in floorGas, in standardGas", "in standardGas, in floorGas", 8, "::local:standardGas@")]
    [TestCase("in gasAvailable, in opcodeGasPrice", "in standardGas, in opcodeGasPrice", 5, "::local:standardGas@")]
    [TestCase("in gasAvailable, in opcodeGasPrice", "in gasAvailable, in premiumPerGas", 6, "::parameter:12:premiumPerGas:")]
    public void Stage_b_prefix_refund_argument_mutations_change_exact_locations(string original, string replacement, int ordinal, string location)
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, SimpleRefundCall, SimpleRefundCall.Replace(original, replacement, StringComparison.Ordinal));
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPrefixSuspension boundary = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan()).Refund;
        StageBPrefixArgument argument = boundary.Call.Arguments.Single(argument => argument.Ordinal == ordinal);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(argument.Mode, Is.EqualTo(StageBArgumentMode.ReadOnlyLocation));
            Assert.That(boundary.OrderedOperands[argument.Child].Children[0].Symbol, Does.Contain(location));
            Assert.That(boundary.OrderedOperands[argument.Child].Children[0].Mode, Is.EqualTo(StageBPrefixOperandMode.ReadOnlyLocation));
        }
    }

    [Test]
    public void Stage_b_prefix_named_arguments_keep_evaluation_order_before_ordinal_binding()
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, SimpleRefundCall, SimpleRefundCall.Replace("in floorGas, in standardGas, postIntrinsicStateReservoir",
            "intrinsicGasStandard: in standardGas, floorGas: in floorGas, postIntrinsicStateReservoir: postIntrinsicStateReservoir", StringComparison.Ordinal));
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPrefixSuspension boundary = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan()).Refund;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(boundary.Call.Arguments.Select(static argument => argument.Ordinal), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 9, 8, 10, 11 }));
            Assert.That(boundary.Call.Arguments.Select(static argument => argument.Child), Is.Ordered);
        }
    }

    [TestCase("codeInsertRefunds: 0", "codeInsertRefunds: 1", 7, "System.UInt64:1")]
    [TestCase("postIntrinsicStateReservoir);", "postIntrinsicStateReservoir + 1);", 10, "System.Int64:1")]
    public void Stage_b_prefix_refund_value_arguments_are_evaluated_terms(string original, string replacement, int ordinal, string constant)
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, SimpleRefundCall, SimpleRefundCall.Replace(original, replacement, StringComparison.Ordinal));
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPrefixSuspension boundary = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan()).Refund;
        StageBPrefixArgument argument = boundary.Call.Arguments.Single(argument => argument.Ordinal == ordinal);
        StageBPrefixNode operand = boundary.OrderedOperands[argument.Child].Children[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(argument.Mode, Is.EqualTo(StageBArgumentMode.Value));
            Assert.That(operand.Mode, Is.EqualTo(StageBPrefixOperandMode.Value));
            Assert.That(PrefixNodes([operand]).Any(node => node.Constant == constant), Is.True);
        }
    }

    private static readonly object[][] PrefixRejectedMutations =
    [
        [SimpleRefundCall, "GasConsumed spentGas = default;", "Stage-B prefix refund-anchor: expected one invocation."],
        [SimpleRefundCall, SimpleRefundCall + "\n            " + SimpleRefundCall.Replace("GasConsumed spentGas = ", "_ = ", StringComparison.Ordinal), "Stage-B prefix refund-anchor: expected one invocation."],
        [SimpleRefundCall, SimpleRefundCall.Replace("postIntrinsicStateReservoir);", "postIntrinsicStateReservoir, topLevelCreateStateGasCharged: true);", StringComparison.Ordinal), "Stage-B prefix refund-default: false required."],
        [SimpleRefundCall, SimpleRefundCall.Replace("= Refund(", "= ((TransactionProcessorBase<TGasPolicy>)this).Refund(", StringComparison.Ordinal), "Stage-B prefix refund-receiver: this required."],
        [SimpleRefundCall, "ReportSimpleTransferAccess(tx, spec, tracer, recipient);\n            " + SimpleRefundCall, "Stage-B prefix after-refund-call: ReportSimpleTransferAccess."],
        ["Metrics.IncrementEmptyCalls();", "Metrics.IncrementEmptyCalls(); int probe = 1; probe += 1;", "Stage-B prefix operation: CompoundAssignment."],
        ["shouldRevert: false,", "shouldRevert: true,", "Stage-B substate-should-revert: expected literal false."],
    ];

    [TestCaseSource(nameof(PrefixRejectedMutations))]
    public void Stage_b_prefix_compile_valid_mutations_fail_closed(string original, string replacement, string diagnostic)
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, original, replacement);
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        Assert.That(() => StageBPrefixCompiler.Compile(fixture.RequireStageBPlan()), Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
    }

    [TestCase("refund: 0,", "refund: 1,", 1, "System.Int64:1")]
    [TestCase("isTracerConnected: false, // safe:", "isTracerConnected: true, // safe:", 5, "bool:true")]
    public void Stage_b_prefix_substate_mutations_change_constructor_operands(string original, string replacement, int ordinal, string constant)
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, original, replacement);
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        StageBPrefixNode constructor = PrefixNodes(prefix).Single(static node => node.Kind == StageBOperationKind.ObjectCreation &&
            node.Call?.Target.Member.Name == ".ctor" && node.Call.Target.Member.DeclaringType == "global::Nethermind.Evm.TransactionSubstate");
        StageBPrefixArgument argument = constructor.Call!.Arguments.Single(argument => argument.Ordinal == ordinal);
        Assert.That(PrefixNodes([constructor.Children[argument.Child]]).Any(node => node.Constant == constant), Is.True);
    }

    [Test]
    public void Stage_b_prefix_action_before_clear_order_is_executable()
    {
        using SourceFixture fixture = new();
        StageBPrefixProgram baseline = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, "TraceSimpleTransferActionStart(tx, recipient, tracer, in value, in gasAvailable);",
            "TGasPolicy.ClearExecutionGas(ref gasAvailable); TraceSimpleTransferActionStart(tx, recipient, tracer, in value, in gasAvailable);");
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPrefixProgram candidate = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        static string[] Calls(StageBPrefixProgram program) => PrefixNodes(program.Functions.Single(static function => function.Signature.Name == "ExecuteSimpleTransfer")
            .Blocks.SelectMany(static block => block.Operations)).Where(static node => node.Call?.Target.Member.Name is "ClearExecutionGas" or "TraceSimpleTransferActionStart")
            .Select(static node => node.Call!.Target.Member.Name).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Calls(baseline), Is.EqualTo(new[] { "TraceSimpleTransferActionStart", "ClearExecutionGas" }));
            Assert.That(Calls(candidate), Is.EqualTo(new[] { "ClearExecutionGas", "TraceSimpleTransferActionStart", "ClearExecutionGas" }));
        }
    }

    [Test]
    public void Stage_b_prefix_rejects_recursive_local_calls()
    {
        using SourceFixture fixture = new();
        string helper = fixture.RequireStageBPlan().Methods.Single(static method => method.Signature?.Name == "TraceSimpleTransferActionStart").Symbol;
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, "tracer.ReportAction(\n                TGasPolicy.GetRemainingGas(in gasAvailable),",
            "TraceSimpleTransferActionStart(tx, recipient, tracer, in value, in gasAvailable);\n            tracer.ReportAction(\n                TGasPolicy.GetRemainingGas(in gasAvailable),");
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        Assert.That(() => StageBPrefixCompiler.Compile(fixture.RequireStageBPlan()), Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Stage-B prefix recursion: " + helper + "."));
    }

    [TestCase("!(result = ValidateSender(tx, header, spec, tracer, opts))", "nonce-boundary")]
    [TestCase("(result = IncrementNonce(tx, header, spec, tracer, opts))", "caller-slice")]
    public void Stage_b_prefix_requires_successful_nonce_anchor(string replacement, string diagnostic)
    {
        using SourceFixture fixture = new();
        string entry = fixture.RequireStageBPlan().PrefixAnchors!.Entry;
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, "!(result = IncrementNonce(tx, header, spec, tracer, opts))", replacement);
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        Assert.That(() => StageBPrefixCompiler.Compile(fixture.RequireStageBPlan()),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Stage-B " + diagnostic + ": " + entry + "."));
    }

    [Test]
    public void Stage_b_prefix_cfg_cycles_have_no_fuel_certificate()
    {
        StageBPlan plan = StageBLowering.Build(FindRepoRoot());
        StageBMethod original = plan.Methods.Single(method => method.Symbol == plan.PrefixAnchors!.Entry);
        StageBCfgPoint entry = original.SelectedEntry!;
        StageBMethod cyclic = original with
        {
            ControlFlowEvidence = original.ControlFlowEvidence.Select(block => block.Ordinal == entry.Block
                ? block with { FallThrough = new(entry.Block, "Regular", [], [], []), Conditional = null } : block).ToArray(),
        };
        StageBPlan candidate = plan with { Methods = plan.Methods.Select(method => method == original ? cyclic : method).ToArray() };
        Assert.That(() => StageBPrefixCompiler.Compile(candidate),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Stage-B prefix cfg-cycle: " + original.Symbol + "."));
    }

    [Test]
    public void Stage_b_prefix_conditional_in_argument_captures_locations_not_value_copies()
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        StageBPrefixFunction simple = prefix.Functions.Single(static function => function.Signature.Name == "ExecuteSimpleTransfer");
        StageBPrefixCaptureMode[] locations = simple.CaptureModes.Where(static capture => capture.Mode == StageBPrefixOperandMode.ReadOnlyLocation).ToArray();
        Assert.That(locations, Is.Not.Empty);
        foreach (StageBPrefixCaptureMode capture in locations)
        {
            StageBPrefixNode[] definitions = PrefixNodes(simple.Blocks.SelectMany(static block => block.Operations))
                .Where(node => node.Kind == StageBOperationKind.FlowCapture && node.Binding.Capture == capture.Capture).ToArray();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(definitions, Is.Not.Empty);
                Assert.That(definitions.All(static definition => definition.Children.Single().Mode == StageBPrefixOperandMode.ReadOnlyLocation), Is.True);
            }
        }
    }

    [Test]
    public void Stage_b_prefix_clear_guard_mutation_changes_executable_successors()
    {
        using SourceFixture fixture = new();
        StageBPrefixProgram baseline = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath,
            "if (newAccountOutOfGas)\n                TGasPolicy.ClearExecutionGas(ref gasAvailable);",
            "if (!newAccountOutOfGas)\n                TGasPolicy.ClearExecutionGas(ref gasAvailable);");
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPrefixProgram candidate = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        static (string Polarity, bool FallThroughClears, bool ConditionalClears) Guard(StageBPrefixProgram program)
        {
            StageBPrefixFunction function = program.Functions.Single(static function => function.Signature.Name == "ExecuteSimpleTransfer");
            int clear = function.Blocks.Single(static block => PrefixNodes(block.Operations)
                .Any(static node => node.Call?.Target.Member.Name == "ClearExecutionGas")).Ordinal;
            StageBPrefixBlock guard = function.Blocks.Single(block => block.BranchValue is not null &&
                (block.FallThrough?.Destination == clear || block.Conditional?.Destination == clear));
            return (guard.ConditionKind, guard.FallThrough?.Destination == clear, guard.Conditional?.Destination == clear);
        }
        Assert.That(Guard(candidate), Is.Not.EqualTo(Guard(baseline)));
    }

    [Test]
    public void Stage_b_prefix_conditional_refund_argument_with_captured_receiver_fails_closed()
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, SimpleRefundCall, SimpleRefundCall.Replace("postIntrinsicStateReservoir);",
            "tracer.IsTracingActions ? (postIntrinsicStateReservoir = postIntrinsicStateReservoir + 1) : (postIntrinsicStateReservoir = postIntrinsicStateReservoir + 2));", StringComparison.Ordinal));
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPlan plan = fixture.RequireStageBPlan();
        Assert.That(() => StageBPrefixCompiler.Compile(plan),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Stage-B prefix refund-receiver: this required."));
    }

    [Test]
    public void Stage_b_prefix_post_refund_effect_is_only_in_the_inert_continuation()
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, SimpleRefundCall, SimpleRefundCall + "\n            Metrics.IncrementEmptyCalls();");
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        StageBPrefixContinuation continuation = prefix.Refund.Continuation;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PrefixNodes(prefix).Count(static node => node.Call?.Target.Member.Name == "IncrementEmptyCalls"), Is.EqualTo(1));
            Assert.That(continuation.Operation, Is.EqualTo(3));
            Assert.That(continuation.SourceBlocks.Single(block => block.Ordinal == continuation.Block).Operations, Has.Length.EqualTo(5));
            Assert.That(Terms(continuation.SourceBlocks.Single(block => block.Ordinal == continuation.Block).Operations.Skip(continuation.Operation + 1))
                .Any(static term => term.Kind == StageBOperationKind.Invocation && term.Symbol.Contains(".IncrementEmptyCalls(", StringComparison.Ordinal)), Is.True);
        }
    }

    [Test]
    public void Stage_b_prefix_post_refund_status_swap_is_retained_only_in_source_continuation()
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath,
            "int statusCode = newAccountOutOfGas ? StatusCode.Failure : StatusCode.Success;",
            "int statusCode = newAccountOutOfGas ? StatusCode.Success : StatusCode.Failure;");
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        StageBPrefixContinuation continuation = prefix.Refund.Continuation;
        StageBTerm failureArm = continuation.SourceBlocks.Single(static block => block.Ordinal == 34)
            .Operations.Single().Children.Single();
        StageBTerm successArm = continuation.SourceBlocks.Single(static block => block.Ordinal == 35)
            .Operations.Single().Children.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((failureArm.Symbol, failureArm.Constant),
                Is.EqualTo(("byte global::Nethermind.Evm.StatusCode.Success", "System.Byte:1")));
            Assert.That((successArm.Symbol, successArm.Constant),
                Is.EqualTo(("byte global::Nethermind.Evm.StatusCode.Failure", "System.Byte:0")));
            Assert.That(prefix.Functions.Single(function => function.Signature.Symbol == continuation.Function)
                .Blocks.Select(static block => block.Ordinal), Does.Not.Contain(34));
        }
    }

    [TestCase("byte-status", "byte statusCode = newAccountOutOfGas ? StatusCode.Failure : StatusCode.Success;")]
    [TestCase("explicit-conversion", "int statusCode = (int)(newAccountOutOfGas ? StatusCode.Failure : StatusCode.Success);")]
    [TestCase("extra-assignment", "int statusCode = newAccountOutOfGas ? StatusCode.Failure : StatusCode.Success;\n            statusCode = StatusCode.Failure;")]
    public void Stage_b_prefix_post_refund_status_assignment_changes_are_retained_only_in_source_continuation(
        string mutation, string replacement)
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath,
            "int statusCode = newAccountOutOfGas ? StatusCode.Failure : StatusCode.Success;", replacement);
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        StageBPrefixContinuation continuation = prefix.Refund.Continuation;
        StageBBlock block36 = continuation.SourceBlocks.Single(static block => block.Ordinal == 36);
        using (Assert.EnterMultipleScope())
        {
            switch (mutation)
            {
                case "byte-status":
                    Assert.That(block36.Operations.Single().Type, Is.EqualTo("byte"));
                    Assert.That(block36.Operations.Single().Children[0].Type, Is.EqualTo("byte"));
                    break;
                case "explicit-conversion":
                    Assert.That(Terms(block36.Operations).Any(static term =>
                        term.Kind == StageBOperationKind.Conversion && !term.Implicit), Is.True);
                    break;
                case "extra-assignment":
                    Assert.That(Terms(continuation.SourceBlocks.SelectMany(static block => block.Operations))
                        .Count(static term => term.Kind == StageBOperationKind.SimpleAssignment &&
                            term.Children.FirstOrDefault()?.Symbol.Contains("::local:statusCode@", StringComparison.Ordinal) == true),
                        Is.EqualTo(2));
                    break;
                default:
                    throw new AssertionException(mutation);
            }
            Assert.That(prefix.Functions.Single(function => function.Signature.Symbol == continuation.Function)
                .Blocks.Select(static block => block.Ordinal), Does.Not.Contain(36));
        }
    }

    [TestCase("actions", "tracer.IsTracingActions")]
    [TestCase("code", "tracer.IsTracingCode")]
    [TestCase("negated", "!tracer.IsTracingAccess")]
    [TestCase("receiver", "NullTxTracer.Instance.IsTracingAccess")]
    public void Stage_b_prefix_post_refund_access_guard_changes_are_retained_only_in_source_continuation(
        string mutation, string condition)
    {
        using SourceFixture fixture = new();
        const string original = "if (tracer.IsTracingAccess)\n            {\n                ReportSimpleTransferAccess(tx, spec, tracer, recipient);\n            }";
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, original,
            original.Replace("tracer.IsTracingAccess", condition, StringComparison.Ordinal));
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        StageBPrefixContinuation continuation = prefix.Refund.Continuation;
        StageBBlock guard = continuation.SourceBlocks.Single(static block => block.Ordinal == 37);
        Assert.That(guard.BranchValue, Is.Not.Null);
        StageBTerm[] terms = Terms([guard.BranchValue!]).ToArray();
        using (Assert.EnterMultipleScope())
        {
            switch (mutation)
            {
                case "actions":
                case "code":
                    Assert.That(guard.BranchValue!.Symbol,
                        Is.EqualTo("bool global::Nethermind.Evm.Tracing.ITxTracer." +
                            (mutation == "actions" ? "IsTracingActions" : "IsTracingCode")));
                    break;
                case "negated":
                    Assert.That(guard.ConditionKind != "WhenFalse" ||
                        terms.Any(static term => term.Kind == StageBOperationKind.Unary && term.Operator.StartsWith("Not;", StringComparison.Ordinal)),
                        Is.True);
                    break;
                case "receiver":
                    Assert.That(terms.Any(static term =>
                        term.Symbol == "global::Nethermind.Evm.Tracing.ITxTracer global::Nethermind.Evm.Tracing.NullTxTracer.Instance"), Is.True);
                    Assert.That(terms.Any(static term => term.Kind == StageBOperationKind.ParameterReference), Is.False);
                    break;
                default:
                    throw new AssertionException(mutation);
            }
            Assert.That(prefix.Functions.Single(function => function.Signature.Symbol == continuation.Function)
                .Blocks.Select(static block => block.Ordinal), Does.Not.Contain(37));
        }
    }

    private static readonly object[][] PostRefundCallFrontierMutations =
    [
        [38, "explicit-receiver", SimpleAccessCall, "this." + SimpleAccessCall],
        [38, "different-tracer", SimpleAccessCall, "ReportSimpleTransferAccess(tx, spec, NullTxTracer.Instance, recipient);"],
        [38, "different-recipient", SimpleAccessCall, "ReportSimpleTransferAccess(tx, spec, tracer, tx.SenderAddress!);"],
        [38, "extra-access-call", SimpleAccessCall, SimpleAccessCall + "\n                " + SimpleAccessCall],
        [38, "return-edge", SimpleAccessCall, SimpleAccessCall + "\n                return TransactionResult.Ok;"],
        [39, "swapped-fee-inputs", SimpleFeeCall, SimpleFeeCall.Replace("premiumPerGas, in opcodeGasPrice", "opcodeGasPrice, in premiumPerGas", StringComparison.Ordinal)],
        [39, "substate-temporary", SimpleFeeCall, SimpleFeeCall.Replace("in substate", "default(TransactionSubstate)", StringComparison.Ordinal)],
        [39, "spent-gas-temporary", SimpleFeeCall, SimpleFeeCall.Replace("in spentGas", "default(GasConsumed)", StringComparison.Ordinal)],
        [39, "premium-temporary", SimpleFeeCall, SimpleFeeCall.Replace("premiumPerGas", "default(UInt256)", StringComparison.Ordinal)],
        [39, "price-temporary", SimpleFeeCall, SimpleFeeCall.Replace("in opcodeGasPrice", "default(UInt256)", StringComparison.Ordinal)],
        [39, "blob-fee-temporary", SimpleFeeCall, SimpleFeeCall.Replace("blobBaseFee", "default(UInt256)", StringComparison.Ordinal)],
        [39, "extra-fee-call", SimpleFeeCall, SimpleFeeCall + "\n            " + SimpleFeeCall],
        [39, "finalize-flags", SimpleFinalizeCall, SimpleFinalizeCall.Replace("restore, commit", "commit, restore", StringComparison.Ordinal)],
        [39, "finalize-spent-gas", SimpleFinalizeCall, SimpleFinalizeCall.Replace("spentGas, statusCode", "default, statusCode", StringComparison.Ordinal)],
        [39, "discarded-finalize-result", SimpleFinalizeCall, SimpleFinalizeCall[7..] + "\n            return TransactionResult.Ok;"],
    ];

    [TestCaseSource(nameof(PostRefundCallFrontierMutations))]
    public void Stage_b_prefix_post_refund_call_frontier_rejects_retained_source_mutations(
        int ordinal, string mutation, string original, string replacement)
    {
        using SourceFixture fixture = new();
        if (original == SimpleFeeCall)
        {
            original += "\n            " + SimpleFinalizeCall;
            replacement += "\n            " + SimpleFinalizeCall;
        }
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, original, replacement);
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing, mutation);
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(fixture.RequireStageBPlan());
        StageBPrefixContinuation continuation = prefix.Refund.Continuation;
        StageBPrefixFunction function = prefix.Functions.Single(function => function.Signature.Symbol == continuation.Function);
        StageBBlock candidate = continuation.SourceBlocks.Single(block => block.Ordinal == ordinal);
        StageBBlock expected = ExpectedPostRefundCallBlock(function, ordinal);
        int temporaryArgument = mutation switch
        {
            "substate-temporary" => 5,
            "spent-gas-temporary" => 6,
            "premium-temporary" => 7,
            "price-temporary" => 8,
            "blob-fee-temporary" => 9,
            _ => -1,
        };
        if (temporaryArgument >= 0)
        {
            StageBTerm argument = candidate.Operations.Single().Children.Single().Children[temporaryArgument + 1];
            Assert.That(argument.Binding, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That((argument.ParameterOrdinal, argument.RefKind), Is.EqualTo((temporaryArgument, "In")));
                Assert.That(argument.Binding!.ArgumentMode, Is.EqualTo(StageBArgumentMode.ReadOnlyTemporary));
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(candidate), JsonSerializer.SerializeToNode(expected)),
                Is.False, mutation);
            Assert.That(function.Blocks.Select(static block => block.Ordinal), Does.Not.Contain(ordinal));
        }
    }

    private static IEnumerable<StageBPrefixNode> PrefixNodes(StageBPrefixProgram prefix) => PrefixNodes(prefix.Functions.SelectMany(static function => function.Blocks)
        .SelectMany(static block => block.BranchValue is null ? block.Operations : [.. block.Operations, block.BranchValue]));

    private static IEnumerable<StageBPrefixNode> PrefixNodes(IEnumerable<StageBPrefixNode> nodes)
    {
        foreach (StageBPrefixNode node in nodes)
        {
            yield return node;
            foreach (StageBPrefixNode child in PrefixNodes(node.Children)) yield return child;
        }
    }

    [Test]
    public void Stage_b_prefix_integrity_pin_matches_compiled_program()
    {
        StageBPrefixProgram prefix = StageBPrefixCompiler.Compile(StageBLowering.Build(FindRepoRoot()));
        string[] vocabulary = PrefixNodes(prefix).Select(static node =>
            $"{node.Kind}|{node.Operator}|{node.Conversion}|{node.Call?.Target.Kind}|{node.Call?.Target.Member.Symbol}")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(prefix.Integrity, Is.EqualTo(StageBArtifact.ExpectedPrefixIntegrity));
            Assert.That(StageBPrefixIntegrity.Compute(prefix), Is.EqualTo(StageBArtifact.ExpectedPrefixIntegrity));
            Assert.That(vocabulary, Is.Not.Empty);
            Assert.That(vocabulary, Is.Unique);
        }
    }

    [Test]
    public void Stage_b_serialized_program_data_is_deterministic_source_bound_and_lean_compilable()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        StageBArtifactResult a = StageBArtifact.Extract(root, first.Location);
        StageBArtifactResult b = StageBArtifact.Extract(root, second.Location);

        byte[] firstIr = File.ReadAllBytes(a.IrPath);
        byte[] firstLean = File.ReadAllBytes(a.LeanPath);
        byte[] firstManifest = File.ReadAllBytes(a.ManifestPath);
        byte[] syntax = File.ReadAllBytes(Path.Combine(root, StageBArtifact.SyntaxRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        StageBArtifactDocument document = StageBArtifact.ParseAndValidate(firstIr);
        StageBArtifact.ValidateBundle(root, firstIr, firstLean, firstManifest, syntax);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstIr, Is.EqualTo(File.ReadAllBytes(b.IrPath)), "IR");
            Assert.That(firstLean, Is.EqualTo(File.ReadAllBytes(b.LeanPath)), "Lean");
            Assert.That(firstManifest, Is.EqualTo(File.ReadAllBytes(b.ManifestPath)), "manifest");
            Assert.That(document.Program.Integrity, Is.EqualTo(StageBArtifact.ExpectedPrefixIntegrity));
        }
        AssertLeanCompiles(Path.Combine(root, "tools", "Evm", "Lean", "SimpleTransferCompletionExtractor"), a.LeanPath);
    }

    [Test]
    public void Stage_b_serialized_program_data_rejects_schema_and_self_rehash_mutations()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory output = new();
        StageBArtifactResult result = StageBArtifact.Extract(root, output.Location);
        byte[] canonical = File.ReadAllBytes(result.IrPath);

        JsonObject unknown = JsonNode.Parse(canonical)!.AsObject();
        unknown["unexpected"] = true;
        JsonObject missing = JsonNode.Parse(canonical)!.AsObject();
        missing.Remove("artifactKind");
        JsonObject nil = JsonNode.Parse(canonical)!.AsObject();
        nil["program"] = null;
        string duplicateText = Encoding.UTF8.GetString(canonical).Replace("  \"schemaVersion\": 1,",
            "  \"schemaVersion\": 1,\n  \"schemaVersion\": 1,", StringComparison.Ordinal);

        foreach (byte[] mutation in new[]
        {
            SerializeJson(unknown), SerializeJson(missing), SerializeJson(nil), Encoding.UTF8.GetBytes(duplicateText),
        })
            Assert.That(() => StageBArtifact.ParseAndValidate(mutation), Throws.TypeOf<ExtractionException>());

        StageBPrefixProgram program = StageBPrefixCompiler.Compile(StageBLowering.Build(root));
        StageBPrefixProgram changed = program with { FuelBound = program.FuelBound + 1, Integrity = "" };
        changed = changed with { Integrity = StageBPrefixIntegrity.Compute(changed) };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed.Integrity, Is.Not.EqualTo(StageBArtifact.ExpectedPrefixIntegrity));
            Assert.That(() => StageBArtifact.ValidateProgram(changed), Throws.TypeOf<ExtractionException>());
        }
    }

    [Test]
    public void Stage_b_serialized_bundle_rejects_manifest_and_syntax_drift()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory output = new();
        StageBArtifactResult result = StageBArtifact.Extract(root, output.Location);
        byte[] ir = File.ReadAllBytes(result.IrPath);
        byte[] lean = File.ReadAllBytes(result.LeanPath);
        byte[] manifest = File.ReadAllBytes(result.ManifestPath);
        byte[] syntax = File.ReadAllBytes(Path.Combine(root, StageBArtifact.SyntaxRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        JsonObject changedPath = JsonNode.Parse(manifest)!.AsObject();
        changedPath["ir"]!["path"] = "other.json";
        JsonObject unknown = JsonNode.Parse(manifest)!.AsObject();
        unknown["unexpected"] = true;
        byte[] changedSyntax = [.. syntax, (byte)' '];
        Assert.That(() => StageBArtifact.ValidateBundle(root, ir, lean, SerializeJson(changedPath), syntax),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => StageBArtifact.ValidateBundle(root, ir, lean, SerializeJson(unknown), syntax),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => StageBArtifact.ValidateBundle(root, ir, lean, manifest, changedSyntax),
            Throws.TypeOf<ExtractionException>());
    }


    [Test]
    public void Stage_b_lowers_live_continuation_and_classifies_its_callees()
    {
        StageBPlan plan = StageBLowering.Build(FindRepoRoot());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Methods, Has.Length.EqualTo(51));
            Assert.That(plan.Methods.All(static method => method.Statements.Length != 0), Is.True);
            Assert.That(plan.Methods.Any(static method => method.ControlFlowEvidence.Length != 0), Is.True);
            Assert.That(plan.Callees.Where(static callee => callee.Owner == StageBCalleeOwner.AcceptedStage)
                .Select(static callee => callee.Contract).Distinct(), Is.EquivalentTo(new[]
                { "transaction-initialization", "state-charge", "ordinary-refund", "receipt-terminal" }));
            Assert.That(plan.Callees.All(static callee => callee.Symbol.Length != 0 && callee.Contract.Length != 0), Is.True);
            Assert.That(plan.Callees.All(static callee => (callee.Owner == StageBCalleeOwner.ExternalRequest) == (callee.RequestKind is not null)), Is.True);
            Assert.That(plan.AcceptedStages, Has.Length.EqualTo(4));
            Assert.That(plan.AcceptedStages.All(static stage => stage.EntryPoints.Length != 0 && stage.SourceClosure.Length != 0), Is.True);
            Assert.That(plan.Callees.Any(static callee => callee.Contract == "CLR value-type default; zero fields and null references" &&
                callee.Symbol.Contains("TransactionResult", StringComparison.Ordinal)), Is.True);
            Assert.That(Terms(plan.Methods.Single(static method => method.Symbol.Contains("TransactionResult.EvmException(", StringComparison.Ordinal)).Statements)
                .Any(static term => term.Kind == StageBOperationKind.Coalesce && term.Children.Any(static child => child.Constant == "string:")), Is.True);
            StageBTerm collection = Terms(plan.Methods.SelectMany(static method => method.Statements)).Single(static term =>
                term.Kind == StageBOperationKind.CollectionExpression && term.Operator.Contains(";add=", StringComparison.Ordinal));
            Assert.That(collection.Symbol, Is.EqualTo("global::Nethermind.Core.Collections.JournalCollection<global::Nethermind.Core.LogEntry>.JournalCollection()"));
            Assert.That(collection.Operator, Does.Contain(";add=void global::Nethermind.Core.Collections.JournalCollection<global::Nethermind.Core.LogEntry>.Add(global::Nethermind.Core.LogEntry item)"));
            Assert.That(plan.Callees.Any(callee => callee.Symbol == collection.Symbol && callee.Owner == StageBCalleeOwner.ExternalRequest), Is.True);
        }
    }

    private static readonly object[][] StageBExecutableMutations =
    [
        [Extractor.EthereumGasPolicyPath, "StateGasSpillRefunded = result.StateGasSpillRefunded,", "StateGasSpillRefunded = result.StateGasSpill,", "TryCreateAvailableFromIntrinsic"],
        [Extractor.TransactionProcessorPath, "premiumPerGas * spentGas", "premiumPerGas + spentGas", "PayFees"],
        [Extractor.TransactionProcessorPath, "tracer.ReportFees(fees, eip1559Fees + blobBaseFee)", "tracer.ReportFees(eip1559Fees + blobBaseFee, fees)", "PayFees"],
        [Extractor.TransferLogPath, "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef", "0xccf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef", "TransferSignature"],
        [Extractor.GasPolicyInterfacePath, "GetNewAccountStateCost() => GasCostOf.NewAccountState;", "GetNewAccountStateCost() => 0;", "GetNewAccountStateCost"],
    ];

    [TestCaseSource(nameof(StageBExecutableMutations))]
    public void Stage_b_compile_valid_source_mutations_change_executable_terms(string path, string original, string replacement, string member)
    {
        using SourceFixture fixture = new();
        StageBPlan baseline = fixture.RequireStageBPlan();
        fixture.ReplaceFirst(path, original, replacement);
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPlan candidate = fixture.RequireStageBPlan();
        StageBMethod Baseline() => baseline.Methods.Single(method => method.Symbol.Contains("." + member + (member == "TransferSignature" ? "" : "("), StringComparison.Ordinal));
        StageBMethod Candidate() => candidate.Methods.Single(method => method.Symbol == Baseline().Symbol);
        Assert.That(JsonSerializer.Serialize(Candidate().Statements), Is.Not.EqualTo(JsonSerializer.Serialize(Baseline().Statements)),
            "Executable operations must change independently of compiler/source hashes and Stage-A descriptive plans.");
        if (member == "TryCreateAvailableFromIntrinsic")
        {
            StageBTerm initializer = Terms(Candidate().Statements).Single(static term => term.Kind == StageBOperationKind.SimpleAssignment &&
                term.Children.FirstOrDefault()?.Symbol.EndsWith(".StateGasSpillRefunded", StringComparison.Ordinal) == true);
            Assert.That(Terms(initializer.Children).Any(static term => term.Symbol.EndsWith(".StateGasSpill", StringComparison.Ordinal)), Is.True);
        }
        if (replacement.Contains("premiumPerGas +", StringComparison.Ordinal))
            Assert.That(Terms(Candidate().Statements).Any(static term => term.Kind == StageBOperationKind.Binary && term.Operator.StartsWith("Add;", StringComparison.Ordinal) &&
                term.Symbol.Contains("operator +", StringComparison.Ordinal)), Is.True);
        if (replacement.StartsWith("tracer.ReportFees", StringComparison.Ordinal))
        {
            StageBTerm invocation = Terms(Candidate().Statements).Single(static term => term.Kind == StageBOperationKind.Invocation && term.Symbol.Contains(".ReportFees(", StringComparison.Ordinal));
            StageBTerm firstArgument = invocation.Children.Single(static term => term.Kind == StageBOperationKind.Argument && term.ParameterOrdinal == 0);
            Assert.That(Terms(firstArgument.Children).Any(static term => term.Kind == StageBOperationKind.Binary && term.Operator.StartsWith("Add;", StringComparison.Ordinal)), Is.True);
        }
    }

    private static readonly object[][] StageBRejectedMutations =
    [
        [Extractor.TransactionProcessorPath, "Metrics.IncrementEmptyCalls();", "Metrics.IncrementEmptyCalls(); ((dynamic)tx).ToString();", "Stage-B operation: DynamicInvocation."],
        [Extractor.TransactionProcessorPath, "Metrics.IncrementEmptyCalls();", "Metrics.IncrementEmptyCalls(); tx.GetType();", "Stage-B callee: global::System.Type object.GetType()."],
        [Extractor.TransactionProcessorPath, "Metrics.IncrementEmptyCalls();", "Metrics.IncrementEmptyCalls(); var boundary = System.DateTime.MinValue;", "Stage-B callee: global::System.DateTime global::System.DateTime.MinValue."],
        [Extractor.TransactionProcessorPath, "Metrics.IncrementEmptyCalls();", "Metrics.IncrementEmptyCalls(); System.Action action = () => { };", "Stage-B callable: ExecuteSimpleTransfer."],
        [Extractor.TransactionProcessorPath, "if (newAccountOutOfGas)", "while (newAccountOutOfGas)", "Stage-B operation: Loop:While."],
        [Extractor.CodeInfoPath, "ReferenceEquals(_analyzer, _emptyAnalyzer)", "false", "Stage-B external-body: bool global::Nethermind.Evm.CodeAnalysis.CodeInfo.IsEmpty."],
        [Extractor.TransactionProcessorPath, "!gasPrice.IsZero && ShouldValidateGas(tx, opts)", "gasPrice.IsZero && ShouldValidateGas(tx, opts)", "Stage-B accepted-stage-dependency: bool global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>.ShouldRefundGas(global::Nethermind.Core.Transaction tx, global::Nethermind.Evm.TransactionProcessing.ExecutionOptions opts, in global::Nethermind.Int256.UInt256 gasPrice)."],
        [Extractor.TransactionProcessorPath, "Metrics.IncrementEmptyCalls();", "Metrics.IncrementEmptyCalls(); int[] probeSource = []; int[] probe = [.. probeSource];", "Stage-B operation: Spread."],
        [Extractor.TransactionProcessorPath, "Metrics.IncrementEmptyCalls();", "Metrics.IncrementEmptyCalls(); System.Collections.Generic.IReadOnlyList<int> probe = [];", "Stage-B collection-construction: global::System.Collections.Generic.IReadOnlyList<int>."],
        [Extractor.TransactionProcessorPath, "Metrics.IncrementEmptyCalls();", "Metrics.IncrementEmptyCalls(); (int, int) probeSource = (1, 2); (long, long) probe = probeSource;", "Stage-B operation: TupleConversion."],
        [Extractor.TransactionProcessorPath, "Metrics.IncrementEmptyCalls();", "Metrics.IncrementEmptyCalls(); if (tx is { Nonce: 0 }) { }", "Stage-B operation: RecursivePattern."],
        [Extractor.TransactionProcessorPath, "Metrics.IncrementEmptyCalls();", "Metrics.IncrementEmptyCalls(); bool probe = (1, 2) == (1, 2);", "Stage-B operation: TupleBinaryOperator."],
    ];

    private static readonly object[][] StageBAdmissionRegressionMutations =
    [
        [Extractor.TransactionProcessorPath, "// Finalize\n            if (restore)", "// Finalize\n            (restore, commit) = (true, false);\n            if (restore)", "Stage-B scope-mutation: (restore, commit) = (true, false)."],
        [Extractor.TransactionProcessorPath, "// Finalize\n            if (restore)", "// Finalize\n            commit |= false;\n            if (restore)", "Stage-B scope-mutation: commit |= false."],
        [Extractor.TransactionProcessorPath, "// Finalize\n            if (restore)", "// Finalize\n            System.Threading.Volatile.Write(ref restore, false);\n            if (restore)", "Stage-B scope-mutation: ref restore."],
        [Extractor.TransactionProcessorPath, "// Finalize\n            if (restore)", "// Finalize\n            bool.TryParse(\"true\", out restore);\n            if (restore)", "Stage-B scope-mutation: out restore."],
        [Extractor.EthereumGasPolicyPath, "public struct EthereumGasPolicy : IGasPolicy<EthereumGasPolicy>\n{", "public struct EthereumGasPolicy : IGasPolicy<EthereumGasPolicy>\n{\n    public static long GetNewAccountStateCost() => 0;", "Stage-B policy-dispatch: long global::Nethermind.Evm.GasPolicy.IGasPolicy<TSelf>.GetNewAccountStateCost()."],
        [Extractor.EthereumGasPolicyPath, "public static void ClearExecutionGas(ref EthereumGasPolicy gas) => gas.Value = 0;", "public static void ClearExecutionGas(ref EthereumGasPolicy gas) => gas.Value = 0;\n    static void IGasPolicy<EthereumGasPolicy>.ClearExecutionGas(ref EthereumGasPolicy gas) => gas.Value = 1;", "Stage-B policy-dispatch: void global::Nethermind.Evm.GasPolicy.IGasPolicy<TSelf>.ClearExecutionGas(ref TSelf gas)."],
        [Extractor.TransferLogPath, "public static class TransferLog\n{", "public static class TransferLog\n{\n    static TransferLog() { TransferSignature = Hash256.Zero; }", "Stage-B static-initialization: global::Nethermind.Evm.TransferLog."],
        [Extractor.TransferLogPath, "public static class TransferLog\n{", "public static class TransferLog\n{\n    private static readonly Hash256 AnotherSignature = Hash256.Zero;", "Stage-B static-initializer-closure: global::Nethermind.Evm.TransferLog."],
        [Extractor.TransferLogPath, "public static readonly Hash256 TransferSignature", "public static Hash256 TransferSignature", "Stage-B static-field-shape: global::Nethermind.Core.Crypto.Hash256 global::Nethermind.Evm.TransferLog.TransferSignature."],
        [Extractor.TransactionSubstatePath, "ILogger logger = default)\n    {", "ILogger logger = default)\n        : this(EvmExceptionType.None, false, null)\n    {", "Stage-B constructor-initializer: : this(EvmExceptionType.None, false, null)."],
        [Extractor.TransactionSubstatePath, "private readonly ILogger _logger;", "private readonly ILogger _logger = default;", "Stage-B constructor-field-initializer: global::Nethermind.Evm.TransactionSubstate."],
    ];

    [TestCase("WorldState.Commit(spec, NullTxTracer.Instance, commitRoots: false);", null)]
    [TestCase("WorldState.Commit(spec, commitRoots: false);", "Stage-B callee: void global::Nethermind.Evm.State.WorldStateExtensions.Commit(global::Nethermind.Evm.State.IWorldState worldState, global::Nethermind.Core.Specs.IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true).")]
    public void Stage_b_admission_keeps_every_post_nonce_effect(string insertion, string? diagnostic)
    {
        using SourceFixture fixture = new();
        StageBPlan baseline = fixture.RequireStageBPlan();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, "Address? simpleTransferRecipient = PrepareSimpleTransferFastPath",
            insertion + "\n            Address? simpleTransferRecipient = PrepareSimpleTransferFastPath");
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        if (diagnostic is not null)
        {
            Assert.That(() => fixture.RequireStageBPlan(), Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
            return;
        }
        StageBPlan candidate = fixture.RequireStageBPlan();
        static StageBMethod Execute(StageBPlan plan) => plan.Methods.Single(static method => method.Symbol.Contains(".Execute(", StringComparison.Ordinal));
        static int Commits(IEnumerable<StageBTerm> terms) => Terms(terms).Count(static term =>
            term.Kind == StageBOperationKind.Invocation && term.Symbol.Contains(".Commit(", StringComparison.Ordinal));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Commits(Execute(candidate).Statements), Is.EqualTo(Commits(Execute(baseline).Statements) + 1));
            Assert.That(Commits(Execute(candidate).ControlFlowEvidence.SelectMany(static block => block.Operations)),
                Is.EqualTo(Commits(Execute(baseline).ControlFlowEvidence.SelectMany(static block => block.Operations)) + 1));
        }
    }

    [TestCaseSource(nameof(StageBRejectedMutations))]
    [TestCaseSource(nameof(StageBAdmissionRegressionMutations))]
    public void Stage_b_compile_valid_mutations_fail_at_exact_boundary(string path, string original, string replacement, string diagnostic)
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(path, original, replacement);
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        Assert.That(() => fixture.RequireStageBPlan(), Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
    }

    private static readonly object[][] StageBHiddenMemberMutations =
    [
        ["StageBProbe probe = [];", "public sealed class StageBProbe : System.Collections.Generic.List<int> { public StageBProbe() => throw new System.Exception(); }",
            "Stage-B callee: global::StageBProbe.StageBProbe()."],
        ["StageBProbe? probe = null; int probeValue = probe ?? 0;", "public sealed class StageBProbe { public static implicit operator int(StageBProbe value) => throw new System.Exception(); }",
            "Stage-B callee: global::StageBProbe.implicit operator int(global::StageBProbe value)."],
        ["StageBProbe probe = null!; (int left, int right) = (probe, probe);", "public sealed class StageBProbe { public static implicit operator int(StageBProbe value) => throw new System.Exception(); }",
            "Stage-B callee: global::StageBProbe.implicit operator int(global::StageBProbe value)."],
        ["StageBProbe probe = null!; JournalCollection<int> probeCollection = [probe];", "public sealed class StageBProbe { public static implicit operator int(StageBProbe value) => throw new System.Exception(); }",
            "Stage-B callee: global::StageBProbe.implicit operator int(global::StageBProbe value)."],
        ["StageBProbe probe = null!; var (left, right) = probe;", "public sealed class StageBProbe { public void Deconstruct(out int left, out int right) => throw new System.Exception(); }",
            "Stage-B callee: void global::StageBProbe.Deconstruct(out int left, out int right)."],
        ["using StageBProbe probe = null!;", "public sealed class StageBProbe : System.IDisposable { public void Dispose() => throw new System.Exception(); }",
            "Stage-B using-resource: global::StageBProbe."],
        ["string probe = \"\" + default(StageBProbe);", "public struct StageBProbe { public override string ToString() => throw new System.Exception(); }",
            "Stage-B operation: StringConcatenationFormatting."],
        ["string probe = default(StageBProbe) + \"\";", "public struct StageBProbe { public override string ToString() => throw new System.Exception(); }",
            "Stage-B operation: StringConcatenationFormatting."],
        ["string probe = \"\"; probe += default(StageBProbe);", "public struct StageBProbe { public override string ToString() => throw new System.Exception(); }",
            "Stage-B operation: StringConcatenationFormatting."],
        ["object probeObject = default(StageBProbe); string probe = \"\" + probeObject;", "public struct StageBProbe { public override string ToString() => throw new System.Exception(); }",
            "Stage-B operation: StringConcatenationFormatting."],
        ["object probeObject = default(StageBProbe); probeObject += \"\";", "public struct StageBProbe { public override string ToString() => throw new System.Exception(); }",
            "Stage-B operation: StringConcatenationFormatting."],
        ["string probe = \"\" + default(StageBProbe);", "public struct StageBProbe { public static implicit operator string(StageBProbe value) => throw new System.Exception(); }",
            "Stage-B callee: global::StageBProbe.implicit operator string(global::StageBProbe value)."],
    ];

    [TestCaseSource(nameof(StageBHiddenMemberMutations))]
    public void Stage_b_hidden_member_mutations_fail_at_exact_boundary(string insertion, string declaration, string diagnostic)
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, "Metrics.IncrementEmptyCalls();", "Metrics.IncrementEmptyCalls(); " + insertion);
        fixture.Append(Extractor.TransactionProcessorPath, "\n" + declaration);
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        Assert.That(() => fixture.RequireStageBPlan(), Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
    }

    [Test]
    public void Stage_b_string_only_concatenation_has_explicit_typed_terms()
    {
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, "Metrics.IncrementEmptyCalls();",
            "Metrics.IncrementEmptyCalls(); string probe = tracer.IsTracingActions ? null! : \"probe\"; string probeCombined = \"prefix\" + probe; probe += \"suffix\";");
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        StageBPlan plan = fixture.RequireStageBPlan();
        StageBMethod method = plan.Methods.Single(static item => item.Symbol.Contains(".ExecuteSimpleTransfer(", StringComparison.Ordinal));
        StageBTerm[] concatenations = Terms(method.Statements).Where(static term => term.Operator.Contains(";stringConcat=string,string", StringComparison.Ordinal)).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(concatenations.Select(static term => term.Kind), Is.EquivalentTo(new[] { StageBOperationKind.Binary, StageBOperationKind.CompoundAssignment }));
            Assert.That(concatenations.All(static term => term.Type == "string" && term.Children.All(static child => child.Type == "string")), Is.True);
        }
    }

    [Test]
    public void Stage_b_rejects_nonterminating_substate_slice()
    {
        using SourceFixture fixture = new();
        string constructor = fixture.RequireStageBPlan().Methods.Single(static method =>
            method.EntryFacts.Any(static fact => fact.StartsWith("shouldRevert=false", StringComparison.Ordinal))).Symbol;
        fixture.ReplaceFirst(Extractor.TransactionSubstatePath, "Error = null;\n            return;", "Error = null;");
        Assert.That(() => fixture.RequireStageBCompilation(), Throws.Nothing);
        Assert.That(() => fixture.RequireStageBPlan(), Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Stage-B substate-slice: " + constructor + "."));
    }

    private static IEnumerable<StageBTerm> Terms(IEnumerable<StageBTerm> terms)
    {
        foreach (StageBTerm term in terms)
        {
            yield return term;
            foreach (StageBTerm nested in Terms(term.Children)) yield return nested;
        }
    }

    [Test]
    public void Real_release_compiler_closure_matches_accepted_refund_pins()
    {
        CompilerClosure closure = CompilerSources.Load(FindRepoRoot());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(closure.Pins.Sources, Has.Length.EqualTo(157));
            Assert.That(closure.Sources, Has.Length.EqualTo(146));
            Assert.That(closure.References, Has.Length.EqualTo(226));
            Assert.That(closure.Compilation.SyntaxTrees.Length, Is.EqualTo(146));
            Assert.That(closure.Compilation.GetTypeByMetadataName("Nethermind.Evm.VirtualMachineStatics"), Is.Not.Null);
        }
    }

    [Test]
    public void Candidate_closure_reports_effective_compiler_source_hashes()
    {
        using SourceFixture fixture = new();
        using TemporaryDirectory baseline = new();
        ExtractionResult original = fixture.Extract(baseline.Location, Path.Combine(baseline.Location, "baseline.lean"));
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, "!senderIsRecipient", "senderIsRecipient");
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        ExtractionResult candidate = fixture.Extract(fixture.Output, Path.Combine(fixture.Output, "candidate.lean"));
        using JsonDocument baselineIr = JsonDocument.Parse(File.ReadAllBytes(original.IrPath), new JsonDocumentOptions { MaxDepth = 1024 });
        using JsonDocument candidateIr = JsonDocument.Parse(File.ReadAllBytes(candidate.IrPath), new JsonDocumentOptions { MaxDepth = 1024 });
        using JsonDocument candidateManifest = JsonDocument.Parse(File.ReadAllBytes(candidate.ManifestPath), new JsonDocumentOptions { MaxDepth = 1024 });
        string OriginalHash(string roster) => baselineIr.RootElement.GetProperty(roster).EnumerateArray()
            .Single(source => source.GetProperty("path").GetString() == Extractor.TransactionProcessorPath).GetProperty("sha256").GetString()!;
        string CandidateHash(string roster) => candidateIr.RootElement.GetProperty(roster).EnumerateArray()
            .Single(source => source.GetProperty("path").GetString() == Extractor.TransactionProcessorPath).GetProperty("sha256").GetString()!;
        Assert.Multiple(() =>
        {
            Assert.That(CandidateHash("compilerSources"), Is.EqualTo(CandidateHash("sources")));
            Assert.That(CandidateHash("compilerSources"), Is.Not.EqualTo(OriginalHash("compilerSources")));
            Assert.That(candidateIr.RootElement.GetProperty("sourceClosureSha256").GetString(),
                Is.Not.EqualTo(baselineIr.RootElement.GetProperty("sourceClosureSha256").GetString()));
            Assert.That(candidateManifest.RootElement.GetProperty("sourceClosureSha256").GetString(),
                Is.EqualTo(candidateIr.RootElement.GetProperty("sourceClosureSha256").GetString()));
        });
    }

    [Test]
    public void Production_sources_extract_deterministically()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        ExtractionResult a = Extractor.Extract(root, first.Location, Path.Combine(first.Location, "first.lean"));
        ExtractionResult b = Extractor.Extract(root, second.Location, Path.Combine(second.Location, "second.lean"));
        Assert.Multiple(() =>
        {
            Assert.That(a.SourceCount, Is.EqualTo(61));
            Assert.That(a.OperationCount, Is.EqualTo(15));
            Assert.That(a.EffectCount, Is.EqualTo(19));
            Assert.That(File.ReadAllBytes(a.IrPath), Is.EqualTo(File.ReadAllBytes(b.IrPath)));
            Assert.That(File.ReadAllBytes(a.ManifestPath), Is.EqualTo(File.ReadAllBytes(b.ManifestPath)));
            Assert.That(File.ReadAllBytes(a.LeanPath), Is.EqualTo(File.ReadAllBytes(b.LeanPath)));
        });
    }

    [Test]
    public void Public_extraction_carries_complete_stage_a_source_plan()
    {
        (byte[] ir, byte[] manifest) = FreshSerializedCandidate();
        using JsonDocument irDocument = JsonDocument.Parse(ir, new JsonDocumentOptions { MaxDepth = 1024 });
        using JsonDocument manifestDocument = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 1024 });
        JsonElement plan = irDocument.RootElement.GetProperty("stageAPlan");
        JsonElement[] methods = plan.GetProperty("methods").EnumerateArray().ToArray();
        JsonElement Method(string member, int arity) => methods.Single(method =>
            method.GetProperty("member").GetString() == member && method.GetProperty("parameterCount").GetInt32() == arity);
        static int TopLevel(JsonElement method) => method.GetProperty("statements").EnumerateArray()
            .Count(statement => statement.GetProperty("parentOrdinal").GetInt32() == -1);
        static int Returns(JsonElement method) => method.GetProperty("effects").EnumerateArray()
            .Count(effect => effect.GetProperty("operationKind").GetString() == "Return");

        byte[] planBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan,
            new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", MaxDepth = 1024 }).TrimEnd('\n') + "\n");
        string planHash = Convert.ToHexStringLower(SHA256.HashData(planBytes));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(methods, Has.Length.EqualTo(19));
            Assert.That(methods.Select(method => (method.GetProperty("path").GetString(),
                method.GetProperty("owner").GetString(), method.GetProperty("member").GetString(),
                method.GetProperty("parameterCount").GetInt32())).Distinct().Count(), Is.EqualTo(19));
            Assert.That((TopLevel(Method("Execute", 6)), Returns(Method("Execute", 6))), Is.EqualTo((14, 5)));
            Assert.That((TopLevel(Method("PrepareSimpleTransferFastPath", 4)), Returns(Method("PrepareSimpleTransferFastPath", 4))),
                Is.EqualTo((6, 2)));
            Assert.That((TopLevel(Method("ExecuteSimpleTransfer", 15)), Returns(Method("ExecuteSimpleTransfer", 15))),
                Is.EqualTo((22, 1)));
            Assert.That(methods.All(method => method.GetProperty("statements").GetArrayLength() > 0 &&
                method.GetProperty("operands").GetArrayLength() > 0 &&
                method.GetProperty("branches").ValueKind == JsonValueKind.Array &&
                method.GetProperty("effects").ValueKind == JsonValueKind.Array), Is.True);
            Assert.That(plan.GetProperty("receiptAdapterProjectionSha256").GetString(),
                Is.EqualTo("910820a7e8bba91c30b052f5818707c46d625a2fbf12727e2aa869512120aa9e"));
            Assert.That(irDocument.RootElement.GetProperty("dependencies").EnumerateArray().Any(dependency =>
                dependency.GetProperty("path").GetString() == Extractor.ReceiptAdapterPath &&
                dependency.GetProperty("sha256").GetString() == plan.GetProperty("receiptAdapterProjectionSha256").GetString()), Is.True);
            Assert.That(manifestDocument.RootElement.GetProperty("stageAPlanSha256").GetString(), Is.EqualTo(planHash));
        }
    }

    private static IEnumerable<TestCaseData> PublicStageAMutations()
    {
        yield return new TestCaseData("sender guard", "!senderIsRecipient", "senderIsRecipient",
            "ExecuteSimpleTransfer", 15, "operands", "", -1)
            .SetName("Public_candidate_serializes_changed_simple_transfer_operand");
        yield return new TestCaseData("process effect", "return ExecuteCore(transaction, txTracer, options);",
            "transaction.GetType(); return ExecuteCore(transaction, txTracer, options);",
            "Process", 3, "effects", "GetType", 1)
            .SetName("Public_candidate_serializes_added_process_effect");
    }

    [TestCaseSource(nameof(PublicStageAMutations))]
    public void Public_candidate_serializes_typed_stage_a_ledger_change(
        string caseName, string original, string replacement, string member, int arity,
        string ledgerName, string expectedToken, int expectedCountDelta)
    {
        using SourceFixture fixture = new();
        using TemporaryDirectory baselineDirectory = new();
        ExtractionResult baseline = fixture.Extract(baselineDirectory.Location,
            Path.Combine(baselineDirectory.Location, "baseline.lean"));
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, original, replacement);
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing, caseName);
        ExtractionResult candidate = fixture.Extract(fixture.Output, Path.Combine(fixture.Output, "candidate.lean"));
        using JsonDocument baselineIr = JsonDocument.Parse(File.ReadAllBytes(baseline.IrPath), new JsonDocumentOptions { MaxDepth = 1024 });
        using JsonDocument candidateIr = JsonDocument.Parse(File.ReadAllBytes(candidate.IrPath), new JsonDocumentOptions { MaxDepth = 1024 });
        using JsonDocument baselineManifest = JsonDocument.Parse(File.ReadAllBytes(baseline.ManifestPath), new JsonDocumentOptions { MaxDepth = 1024 });
        using JsonDocument candidateManifest = JsonDocument.Parse(File.ReadAllBytes(candidate.ManifestPath), new JsonDocumentOptions { MaxDepth = 1024 });
        static JsonElement Method(JsonDocument document, string methodName, int parameterCount) =>
            document.RootElement.GetProperty("stageAPlan").GetProperty("methods").EnumerateArray()
                .Single(method => method.GetProperty("owner").GetString() == "TransactionProcessorBase" &&
                    method.GetProperty("member").GetString() == methodName &&
                    method.GetProperty("parameterCount").GetInt32() == parameterCount);
        JsonElement admittedMethod = Method(baselineIr, member, arity);
        JsonElement changedMethod = Method(candidateIr, member, arity);
        string[] admittedNodes = admittedMethod.GetProperty(ledgerName).EnumerateArray()
            .Select(static node => node.GetProperty("operation").GetRawText()).ToArray();
        string[] changedNodes = changedMethod.GetProperty(ledgerName).EnumerateArray()
            .Select(static node => node.GetProperty("operation").GetRawText()).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changedNodes, Is.Not.EqualTo(admittedNodes), caseName + " typed operation nodes");
            Assert.That(changedNodes.Length - admittedNodes.Length, Is.EqualTo(expectedCountDelta), caseName + " ledger count");
            string companionLedger = member == "ExecuteSimpleTransfer" ? "branches" : "statements";
            Assert.That(changedMethod.GetProperty(companionLedger).EnumerateArray()
                    .Select(static node => node.GetProperty("operation").GetRawText()).ToArray(),
                Is.Not.EqualTo(admittedMethod.GetProperty(companionLedger).EnumerateArray()
                    .Select(static node => node.GetProperty("operation").GetRawText()).ToArray()),
                caseName + " companion typed ledger");
            if (expectedToken.Length != 0)
                Assert.That(string.Join('\n', changedNodes), Does.Contain(expectedToken), caseName + " resolved operation");
            Assert.That(candidateIr.RootElement.GetProperty("stageAPlan").GetRawText(),
                Is.Not.EqualTo(baselineIr.RootElement.GetProperty("stageAPlan").GetRawText()), caseName + " serialized plan");
            Assert.That(candidateManifest.RootElement.GetProperty("stageAPlanSha256").GetString(),
                Is.Not.EqualTo(baselineManifest.RootElement.GetProperty("stageAPlanSha256").GetString()), caseName + " plan identity");
        }
    }

    [Test]
    public void Stage_a_rejects_compile_valid_dynamic_invocation_in_process()
    {
        using SourceFixture fixture = new();
        Assert.That(() => fixture.RequireStageASourcePlan(), Throws.Nothing);
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath,
            "return ExecuteCore(transaction, txTracer, options);",
            "((dynamic)transaction).ToString(); return ExecuteCore(transaction, txTracer, options);");
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.RequireStageASourcePlan(),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                "Stage-A TransactionProcessorBase.Process contains unsupported deferred, callable, or dynamic operation DynamicInvocation."));
    }

    [TestCase("parameterName")]
    [TestCase("argumentKind")]
    [TestCase("refKind")]
    public void Missing_default_empty_stage_a_operation_field_is_rejected(string property)
    {
        (byte[] ir, _) = FreshSerializedCandidate();
        JsonObject mutation = JsonNode.Parse(ir, documentOptions: new JsonDocumentOptions { MaxDepth = 1024 })!.AsObject();
        JsonObject operation = mutation["stageAPlan"]!["methods"]![0]!["statements"]![0]!["operation"]!.AsObject();
        Assert.That(operation[property]!.GetValue<string>(), Is.Empty);
        operation.Remove(property);
        byte[] candidate = Encoding.UTF8.GetBytes(mutation.ToJsonString());
        string diagnostic = "Simple-transfer typed AST node has a missing or wrongly typed property: " + property;
        Assert.That(() => Extractor.ValidateSerializedIr(ir, candidate),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
        Assert.That(() => Extractor.EmitLeanForTest(candidate),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
    }

    private static IEnumerable<TestCaseData> StageAMutations()
    {
        yield return new TestCaseData(Extractor.TransactionProcessorPath,
            "simpleTransferRecipient is null || restore || tracer.IsTracingState",
            "simpleTransferRecipient is null || restore || tracer.IsTracingActions",
            "Stage-A typed source plan changed: TransactionProcessorBase.Execute/6.")
            .SetName("Stage_A_rejects_precommit_tracing_state_mutation");
        yield return new TestCaseData(Extractor.TransactionProcessorPath,
            "return HasNoExecutableCode(preloadedCodeInfo, preloadedDelegationAddress) ? recipient : null;",
            "return !HasNoExecutableCode(preloadedCodeInfo, preloadedDelegationAddress) ? recipient : null;",
            "Stage-A typed source plan changed: TransactionProcessorBase.PrepareSimpleTransferFastPath/4.")
            .SetName("Stage_A_rejects_prepare_recipient_mutation");
        yield return new TestCaseData(Extractor.TransactionProcessorPath,
            "Metrics.IncrementEmptyCalls();", "Metrics.IncrementEmptyCalls(); Metrics.IncrementEmptyCalls();",
            "Stage-A TransactionProcessorBase.ExecuteSimpleTransfer/15 lost its complete statement or return skeleton.")
            .SetName("Stage_A_rejects_added_simple_transfer_effect");
        yield return new TestCaseData(Extractor.EthereumGasPolicyPath,
            "if (result.Outcome is StateGasChargeOutcome.OutOfGas) return false;\n\n" +
            "        gas.Value = result.Value;\n        gas.StateReservoir = result.StateReservoir;\n" +
            "        gas.StateGasUsed = result.StateGasUsed;\n        gas.StateGasSpill = result.StateGasSpill;",
            "if (result.Outcome is StateGasChargeOutcome.OutOfGas) return false;\n\n" +
            "        gas.Value = result.Value;\n        gas.StateReservoir = result.StateReservoir;\n" +
            "        gas.StateGasUsed = result.StateGasUsed;\n        gas.StateGasSpill = result.StateGasSpillRefunded;",
            "Stage-A typed source plan changed: EthereumGasPolicy.TryConsumeStateGas/2.")
            .SetName("Stage_A_rejects_state_charge_copy_back_mutation");
        yield return new TestCaseData(Extractor.TransactionProcessorPath,
            "codeInsertRefunds: 0, in floorGas", "codeInsertRefunds: 1, in floorGas",
            "Stage-A typed source plan changed: TransactionProcessorBase.ExecuteSimpleTransfer/15.")
            .SetName("Stage_A_rejects_refund_argument_mutation");
    }

    [TestCaseSource(nameof(StageAMutations))]
    public void Stage_a_compile_valid_mutation_has_named_diagnostic(
        string path, string original, string replacement, string expectedDiagnostic)
    {
        using SourceFixture fixture = new();
        Assert.That(() => fixture.RequireStageASourcePlan(), Throws.Nothing);
        fixture.ReplaceFirst(path, original, replacement);
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.RequireStageASourcePlan(),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(expectedDiagnostic));
    }

    [Test]
    public void Checked_in_artifacts_remain_stale_until_reviewed_regeneration()
    {
        string root = FindRepoRoot();
        string package = Path.Combine(root, "tools", "Evm", "Lean", "SimpleTransferCompletionExtractor");
        string generated = Path.Combine(package, "Generated");
        using JsonDocument checkedIn = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generated, "SimpleTransferCompletion.ir.json")),
            new JsonDocumentOptions { MaxDepth = 1024 });
        Assert.That(checkedIn.RootElement.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
        Assert.That(checkedIn.RootElement.GetProperty("acceptanceState").GetString(), Is.EqualTo("bounded-source-extraction-and-refinement-only"));
        Assert.That(() => Extractor.ValidateExistingArtifacts(root, generated, Path.Combine(generated, "SimpleTransferCompletion.lean")),
            Throws.TypeOf<ExtractionException>().With.Message.StartWith("Generated simple-transfer IR drifted:"));
    }

    [Test]
    public void Generated_model_is_theorem_free_and_keeps_receipt_input()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory candidate = new();
        ExtractionResult result = Extractor.Extract(root, candidate.Location, Path.Combine(candidate.Location, "SimpleTransferCompletion.lean"));
        string generated = File.ReadAllText(result.LeanPath);
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain("structure ReceiptContinuationInput"));
            Assert.That(generated, Does.Contain("structure SimpleComplete"));
            Assert.That(generated, Does.Contain("def run (input : CompletionInput)"));
            Assert.That(generated, Does.Contain("Metrics"), "source metadata should retain metric binding");
            Assert.That(generated, Does.Contain("sourceNewAccountPredicate"));
            Assert.That(generated, Does.Contain("sourceRecipientPredicate"));
            Assert.That(generated, Does.Contain("structure SourceOperationSpec"));
            Assert.That(generated, Does.Contain("sourceOperationAvailable"));
            Assert.That(generated, Does.Contain("sourceAcceptanceState"));
            Assert.That(generated, Does.Contain("request-boundary-only"));
            Assert.That(generated, Does.Contain("typedAst="));
            Assert.That(generated, Does.Contain("def transferDataFor"));
            Assert.That(generated, Does.Contain("def addressHashProjection"));
            Assert.That(generated, Does.Contain("topics := [sourceTransferSignature, addressHashProjection input.handoff.tx.sender, addressHashProjection input.handoff.recipient]"));
            Assert.That(generated, Does.Contain("normalReturn : Bool"));
            Assert.That(generated, Does.Not.Contain("accessObservation : AccessObservation"));
            Assert.That(generated, Does.Not.Contain("transferLog : TransferLog"));
            Assert.That(generated, Does.Not.Contain("TransferPayloadAdapter"));
            Assert.That(generated, Does.Not.Contain("fromTopic"));
            Assert.That(generated, Does.Not.Contain("toTopic"));
            Assert.That(generated, Does.Not.Contain("theorem "));
            Assert.That(generated, Does.Not.Contain("axiom "));
            Assert.That(generated, Does.Not.Contain("sorry"));
            Assert.That(generated.Split('\n').Any(line => line.TrimStart().StartsWith("admit ", StringComparison.Ordinal)), Is.False);
        });
    }

    [Test]
    public void Ir_keeps_completion_order_and_adapter_boundary()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory candidate = new();
        ExtractionResult result = Extractor.Extract(root, candidate.Location, Path.Combine(candidate.Location, "SimpleTransferCompletion.lean"));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(result.IrPath), new JsonDocumentOptions { MaxDepth = 1024 });
        JsonElement completion = document.RootElement.GetProperty("completion");
        string[] stages = completion.GetProperty("stages").EnumerateArray().Select(stage => stage.GetProperty("id").GetString()!).ToArray();
        string[] effects = completion.GetProperty("effects").EnumerateArray().Select(effect => effect.GetProperty("id").GetString()!).ToArray();
        JsonElement operations = completion.GetProperty("operations");
        Assert.That(stages, Is.EqualTo(new[] { "metrics", "stateCharge", "valueAndRecipient", "actionStart", "oogForfeit", "transferLog", "substate", "actionEnd", "refundSettlement", "accessReport", "headerFees", "finalize", "receiptContinuation" }));
        Assert.That(document.RootElement.GetProperty("acceptanceState").GetString(), Is.EqualTo("hash-pinned-audited-handwritten-model-to-model-request-boundary-only"));
        Assert.That(effects, Does.Contain("metricsIncrement"));
        Assert.That(effects, Does.Contain("recipientBalance"));
        Assert.That(effects, Does.Contain("receiptContinuation"));
        Assert.That(completion.GetProperty("handoff").GetString(), Is.EqualTo("TransactionProcessorBase.Execute/6 -> ExecuteSimpleTransfer/15"));
        Assert.That(completion.GetProperty("receiptMode").GetString(), Is.EqualTo("receipt-continuation-input-only"));
        Assert.That(completion.GetProperty("adapters").GetArrayLength(), Is.EqualTo(6));
        string[] callerRoles = completion.GetProperty("methodBindings").EnumerateArray()
            .Select(binding => binding.GetProperty("role").GetString()!).ToArray();
        Assert.That(callerRoles, Does.Contain("caller simple-transfer dispatch"));
        Assert.That(callerRoles, Does.Contain("normal code lookup with fork-selected delegation"));
        Assert.That(document.RootElement.GetProperty("dependencies").EnumerateArray()
            .Any(dependency => dependency.GetProperty("path").GetString()!.Contains("OrdinaryPostNonceDispatchExtractor", StringComparison.Ordinal)), Is.False);
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            JsonElement lowering = operation.GetProperty("lowering");
            Assert.That(lowering.GetProperty("grammar").GetString(), Is.Not.Empty);
            Assert.That(lowering.GetProperty("receiver").GetString(), Is.Not.Empty);
            Assert.That(lowering.GetProperty("targetSymbolIdentity").GetString(), Is.EqualTo(operation.GetProperty("binding").GetProperty("targetSymbolIdentity").GetString()));
            Assert.That(lowering.GetProperty("executionTerm").GetString(), Is.Not.Empty);
            Assert.That(lowering.GetProperty("returnType").GetString(), Is.Not.Empty);
            Assert.That(lowering.GetProperty("argumentNames").GetArrayLength(), Is.EqualTo(lowering.GetProperty("argumentTypes").GetArrayLength()));
            Assert.That(lowering.GetProperty("argumentNames").GetArrayLength(), Is.EqualTo(lowering.GetProperty("argumentKinds").GetArrayLength()));
        }
    }

    [Test]
    public void Public_extraction_rejects_source_drift()
    {
        AssertUnmutatedBaselineExtracts();
        using SourceFixture fixture = new();
        fixture.Replace(Extractor.TransactionProcessorPath, "!senderIsRecipient", "senderIsRecipient");
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.ExtractPinned(fixture.Output, Path.Combine(fixture.Output, "drift.lean")),
            Throws.TypeOf<ExtractionException>().With.Message.StartWith("Source drift in " + Extractor.TransactionProcessorPath + ":"));
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [Test]
    public void Unsupported_source_mutation_fails_closed_without_artifacts()
    {
        AssertUnmutatedBaselineExtracts();
        using SourceFixture fixture = new();
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath, "if (newAccountOutOfGas)", "while (newAccountOutOfGas)");
        AssertCompileValidMutationRejected(fixture, "unsupported",
            "Unsupported syntax WhileStatement in admitted completion member ExecuteSimpleTransfer.");
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [Test]
    public void Typed_source_mutation_changes_rebound_ir_and_live_lean()
    {
        string root = FindRepoRoot();
        using SourceFixture fixture = new();
        fixture.Replace(Extractor.TransactionProcessorPath, "!senderIsRecipient", "senderIsRecipient");
        using TemporaryDirectory baselineDirectory = new();
        ExtractionResult baseline = Extractor.Extract(root, baselineDirectory.Location, Path.Combine(baselineDirectory.Location, "baseline.lean"));
        ExtractionResult mutated = fixture.Extract(fixture.Output, Path.Combine(fixture.Output, "mutated.lean"));
        Assert.That(File.ReadAllBytes(mutated.IrPath), Is.Not.EqualTo(File.ReadAllBytes(baseline.IrPath)));
        Assert.That(File.ReadAllBytes(mutated.LeanPath), Is.Not.EqualTo(File.ReadAllBytes(baseline.LeanPath)));
        Assert.That(File.ReadAllText(mutated.LeanPath), Does.Contain("sourceNewAccountPredicate : SourcePredicate :="));
        Assert.That(File.ReadAllText(mutated.LeanPath), Does.Contain(".selfSend"));
    }

    [Test]
    public void Serialized_tampering_is_rejected()
    {
        (byte[] ir, byte[] manifest) = FreshSerializedCandidate();
        JsonObject irMutation = JsonNode.Parse(ir, documentOptions: new JsonDocumentOptions { MaxDepth = 1024 })!.AsObject();
        irMutation["completion"]!["effects"]![0]!["id"] = "tampered";
        JsonObject manifestMutation = JsonNode.Parse(manifest, documentOptions: new JsonDocumentOptions { MaxDepth = 1024 })!.AsObject();
        manifestMutation["semanticIrSha256"] = new string('0', 64);
        Assert.That(() => Extractor.ValidateSerializedIr(ir, Encoding.UTF8.GetBytes(irMutation.ToJsonString())),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Candidate simple-transfer IR differs from the admitted model-boundary IR."));
        Assert.That(() => Extractor.ValidateSerializedManifest(manifest, Encoding.UTF8.GetBytes(manifestMutation.ToJsonString())),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Candidate simple-transfer manifest differs from the admitted model-boundary manifest."));
        string manifestText = Encoding.UTF8.GetString(manifest);
        byte[] duplicate = Encoding.UTF8.GetBytes("{\"schemaVersion\":999," + manifestText[1..]);
        byte[] wrongCase = Encoding.UTF8.GetBytes(manifestText.Replace("\"schemaVersion\"", "\"SchemaVersion\"", StringComparison.Ordinal));
        byte[] caseVariantDuplicate = Encoding.UTF8.GetBytes("{\"SchemaVersion\":999," + manifestText[1..]);
        byte[] nestedDuplicate = Encoding.UTF8.GetBytes(manifestText.Replace(
            "\"path\": \"src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs\"",
            "\"path\": \"injected\", \"path\": \"src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs\"",
            StringComparison.Ordinal));
        Assert.That(nestedDuplicate, Is.Not.EqualTo(manifest));
        Assert.That(() => Extractor.ValidateSerializedManifest(manifest, duplicate),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Duplicate simple-transfer JSON property: schemaVersion"));
        Assert.That(() => Extractor.ValidateSerializedManifest(manifest, wrongCase),
            Throws.TypeOf<ExtractionException>().With.Message.StartWith("Simple-transfer manifest is not valid JSON:"));
        Assert.That(() => Extractor.ValidateSerializedManifest(manifest, caseVariantDuplicate),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Duplicate simple-transfer JSON property: schemaVersion"));
        Assert.That(() => Extractor.ValidateSerializedManifest(manifest, nestedDuplicate),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Duplicate simple-transfer JSON property: path"));
    }

    [TestCase("typeIdentity", false)]
    [TestCase("kind", false)]
    [TestCase("type", true)]
    [TestCase("children", true)]
    public void Incomplete_or_wrongly_typed_nested_ast_is_rejected(string property, bool wrongType)
    {
        (byte[] ir, _) = FreshSerializedCandidate();
        JsonObject mutation = JsonNode.Parse(ir, documentOptions: new JsonDocumentOptions { MaxDepth = 1024 })!.AsObject();
        JsonObject nested = mutation["completion"]!["methodBindings"]![0]!["typedAst"]!["children"]![0]!.AsObject();
        if (wrongType) nested[property] = property == "children" ? JsonValue.Create("wrong") : JsonValue.Create(1);
        else nested.Remove(property);

        byte[] candidate = Encoding.UTF8.GetBytes(mutation.ToJsonString());
        string diagnostic = property == "children"
            ? "Simple-transfer typed AST node has missing or wrongly typed children."
            : "Simple-transfer typed AST node has a missing or wrongly typed property: " + property;
        Assert.That(() => Extractor.ValidateSerializedIr(ir, candidate),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
        Assert.That(() => Extractor.EmitLeanForTest(candidate),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
    }

    [TestCase("typeIdentity", false)]
    [TestCase("kind", false)]
    [TestCase("type", true)]
    [TestCase("children", true)]
    public void Incomplete_or_wrongly_typed_manifest_ast_is_rejected(string property, bool wrongType)
    {
        (_, byte[] manifest) = FreshSerializedCandidate();
        JsonObject mutation = JsonNode.Parse(manifest, documentOptions: new JsonDocumentOptions { MaxDepth = 1024 })!.AsObject();
        JsonObject nested = mutation["bindings"]![0]!["typedAst"]!["children"]![0]!.AsObject();
        if (wrongType) nested[property] = property == "children" ? JsonValue.Create("wrong") : JsonValue.Create(1);
        else nested.Remove(property);

        byte[] candidate = Encoding.UTF8.GetBytes(mutation.ToJsonString());
        string diagnostic = property == "children"
            ? "Simple-transfer typed AST node has missing or wrongly typed children."
            : "Simple-transfer typed AST node has a missing or wrongly typed property: " + property;
        Assert.That(() => Extractor.ValidateSerializedManifest(manifest, candidate),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
    }

    [Test]
    public void Caller_expression_search_excludes_lambda_shadow() =>
        Assert.That(() => Extractor.RequireOwnedExpressionForTest(
            "void M() { bool x = false; System.Action hidden = () => { bool y = x && true; }; }", "x && true"),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Simple-transfer caller expression changed: M/x && true."));

    [TestCase("void M() { void Local() { Hit(); } }", TestName = "Caller_invocation_search_excludes_local_function_shadow")]
    [TestCase("void M() { System.Action hidden = () => Hit(); }", TestName = "Caller_invocation_search_excludes_lambda_shadow")]
    [TestCase("void M() { System.Action hidden = delegate { Hit(); }; }", TestName = "Caller_invocation_search_excludes_anonymous_method_shadow")]
    public void Caller_invocation_search_excludes_nested_callable(string methodSource)
    {
        Assert.That(() => Extractor.RequireOwnedInvocationForTest(methodSource, "Hit"),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Expected invocation Hit/0 in M, found 0."));
        Assert.That(() => Extractor.RequireCfgOwnershipForTest(methodSource, "Hit"),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Required test source node is inside a nested callable."));
    }

    [Test]
    public void Unsupported_while_has_named_syntax_diagnostic() =>
        Assert.That(() => Extractor.RequireFiniteSyntaxForTest("void M() { while (true) break; }"),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo("Unsupported syntax WhileStatement in admitted completion member M."));

    [TestCase("target", "ExecuteEvmCall must retain exactly four no-frame gotos, found 3.")]
    [TestCase("guard", "no-frame goto 1 lost its direct final guard/effect position.")]
    [TestCase("nested", "ExecuteEvmCall must retain exactly four no-frame gotos, found 3.")]
    [TestCase("fail-bypass", "FailContractCreate must retain exactly one goto Complete bypass, found 0.")]
    public void No_frame_branch_mutations_have_named_compile_valid_diagnostics(string mutation, string diagnostic)
    {
        using SourceFixture fixture = new();
        using TemporaryDirectory baseline = new();
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(baseline.Location, Path.Combine(baseline.Location, "baseline.lean")), Throws.Nothing);
        switch (mutation)
        {
            case "target":
                fixture.ReplaceFirst(Extractor.TransactionProcessorPath, "goto CompleteWithoutFrame;", "goto Complete;");
                break;
            case "guard":
                fixture.ReplaceFirst(Extractor.TransactionProcessorPath,
                    "if (topFrameOutOfGas)\n            {\n                TraceHaltedTopFrameAction",
                    "if (false && topFrameOutOfGas)\n            {\n                TraceHaltedTopFrameAction");
                break;
            case "nested":
                fixture.ReplaceFirst(Extractor.TransactionProcessorPath, "goto CompleteWithoutFrame;",
                    "void Hidden() { goto HiddenLabel; HiddenLabel: return; } Hidden();");
                break;
            case "fail-bypass":
                fixture.ReplaceFirst(Extractor.TransactionProcessorPath, "goto Complete;\n        CompleteWithoutFrame:",
                    "goto CompleteWithoutFrame;\n        CompleteWithoutFrame:");
                break;
            default:
                throw new AssertionException("Unknown no-frame mutation.");
        }
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(fixture.Output, Path.Combine(fixture.Output, "no-frame.lean")),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [TestCase(Extractor.GasCostOfPath, "CostPerStateByte = 1530", "CostPerStateByte = 1531",
        "GasCostOf.NewAccountState source formula and accepted Core metadata disagree.")]
    [TestCase(Extractor.AddressPath, "SystemUserHex = \"0xfffffffffffffffffffffffffffffffffffffffe\"",
        "SystemUserHex = \"0xfffffffffffffffffffffffffffffffffffffffd\"",
        "Address.SystemUser source initializer and accepted Core metadata disagree.")]
    [TestCase(Extractor.AddressPath, "SystemUser { get; } = new(SystemUserHex)",
        "SystemUser { get; } = new(\"0xfffffffffffffffffffffffffffffffffffffffe\")",
        "Address.SystemUser source initializer and accepted Core metadata disagree.")]
    [TestCase(Extractor.GasPolicyInterfacePath, "GetNewAccountStateCost() => GasCostOf.NewAccountState",
        "GetNewAccountStateCost() => GasCostOf.CreateState",
        "IGasPolicy.GetNewAccountStateCost no longer returns the exact GasCostOf.NewAccountState schedule field.")]
    [TestCase(Extractor.TransferLogPath, "Sender = Address.SystemUser", "Sender = Address.MaxValue",
        "TransferLog.Sender no longer binds the exact Address.SystemUser metadata property.")]
    [TestCase(Extractor.MainnetDiPath, ".AddScoped<IWorldState, WorldState>()",
        ".AddScoped<IWorldState, WorldState>().AddScoped<IWorldState, WorldState>()",
        "Expected one DI registration AddScoped<IWorldState,WorldState>(), found 2.")]
    public void Core_and_Init_projection_mutations_have_named_diagnostics(
        string path, string original, string replacement, string diagnostic)
    {
        using SourceFixture fixture = new();
        using TemporaryDirectory baseline = new();
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(baseline.Location, Path.Combine(baseline.Location, "baseline.lean")), Throws.Nothing);
        fixture.ReplaceFirst(path, original, replacement);
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(fixture.Output, Path.Combine(fixture.Output, "projection.lean")),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [TestCase("polarity")]
    [TestCase("false-arm")]
    public void Eip8037_cumulative_counter_mutations_have_named_compile_valid_diagnostics(string mutation)
    {
        using SourceFixture fixture = new();
        using TemporaryDirectory baseline = new();
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(baseline.Location, Path.Combine(baseline.Location, "baseline.lean")), Throws.Nothing);
        switch (mutation)
        {
            case "polarity":
                fixture.ReplaceFirst(Extractor.TransactionProcessorPath,
                    "if (spec.IsEip8037Enabled)\n                {\n                    _blockCumulativeExecutionGas += spentGas.EffectiveBlockGas;",
                    "if (!spec.IsEip8037Enabled)\n                {\n                    _blockCumulativeExecutionGas += spentGas.EffectiveBlockGas;");
                break;
            case "false-arm":
                fixture.ReplaceFirst(Extractor.TransactionProcessorPath,
                    "_blockCumulativeExecutionGas += spentGas.EffectiveBlockGas;\n                    _blockCumulativeStateGas += spentGas.BlockStateGas;\n                    header.GasUsed = TGasPolicy.CombineBlockGas(_blockCumulativeExecutionGas, _blockCumulativeStateGas);\n                }\n                else\n                {\n                    header.GasUsed += spentGas.EffectiveBlockGas;",
                    "header.GasUsed += spentGas.EffectiveBlockGas;\n                }\n                else\n                {\n                    _blockCumulativeExecutionGas += spentGas.EffectiveBlockGas;\n                    _blockCumulativeStateGas += spentGas.BlockStateGas;\n                    header.GasUsed = TGasPolicy.CombineBlockGas(_blockCumulativeExecutionGas, _blockCumulativeStateGas);");
                break;
            default:
                throw new AssertionException("Unknown EIP-8037 cumulative counter mutation.");
        }
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(fixture.Output, Path.Combine(fixture.Output, "cumulative-counter.lean")),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                "UpdateHeaderGasUsedAndPayFees EIP-8037 cumulative counter guard or true-arm effects changed."));
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [Test]
    public void State_hooks_remain_unresolved_external_normal_return_premises()
    {
        (byte[] ir, byte[] manifest) = FreshSerializedCandidate();
        JsonObject mutation = JsonNode.Parse(ir, documentOptions: new JsonDocumentOptions { MaxDepth = 1024 })!.AsObject();
        JsonObject hook = mutation["completion"]!["methodBindings"]!.AsArray()
            .Select(static node => node!.AsObject())
            .First(binding => binding["path"]!.GetValue<string>() == Extractor.WorldStatePath);
        Assert.Multiple(() =>
        {
            Assert.That(hook["operationKind"]!.GetValue<string>(), Is.EqualTo("ExternalHookPremise"));
            Assert.That(hook["symbolResolved"]!.GetValue<bool>(), Is.False);
            Assert.That(hook["isReachable"]!.GetValue<bool>(), Is.False);
        });
        hook["operationKind"] = "ExactExternalSourceProjection";
        Assert.That(() => Extractor.ValidateSerializedIr(ir, Encoding.UTF8.GetBytes(mutation.ToJsonString())),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                $"External source binding mode or identity changed: {Extractor.WorldStatePath}/{hook["member"]!.GetValue<string>()}."));

        JsonObject manifestMutation = JsonNode.Parse(manifest, documentOptions: new JsonDocumentOptions { MaxDepth = 1024 })!.AsObject();
        JsonObject manifestHook = manifestMutation["bindings"]!.AsArray().Select(static node => node!.AsObject())
            .First(binding => binding["path"]!.GetValue<string>() == Extractor.WorldStatePath);
        manifestHook["operationKind"] = "ExactExternalSourceProjection";
        Assert.That(() => Extractor.ValidateSerializedManifest(manifest, Encoding.UTF8.GetBytes(manifestMutation.ToJsonString())),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                $"External source binding mode or identity changed: {Extractor.WorldStatePath}/{manifestHook["member"]!.GetValue<string>()}."));
    }

    [Test]
    public void Required_EVM_source_cannot_fall_back_to_external_projection() =>
        Assert.That(() => Extractor.RequireMissingCompiledEvmTreeForTest(FindRepoRoot()),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                "Required EVM source missing from compiled Roslyn tree roster: " + Extractor.TransactionProcessorPath));

    [Test]
    public void Shadow_processor_type_is_compile_valid_but_rejected_by_lineage()
    {
        using SourceFixture fixture = new();
        using TemporaryDirectory baseline = new();
        Assert.That(() => fixture.Extract(baseline.Location, Path.Combine(baseline.Location, "baseline.lean")), Throws.Nothing);
        fixture.Append(Extractor.TransactionProcessorPath,
            "\nnamespace Shadow { internal sealed class EthereumTransactionProcessor {} }\n");
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(fixture.Output, Path.Combine(fixture.Output, "shadow.lean")),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                "Expected one type EthereumTransactionProcessor in " + Extractor.TransactionProcessorPath + ", found 2."));
    }

    [TestCase("unsealed", "Standard Ethereum processor source lineage or shadow identity changed.")]
    [TestCase("override", "Standard Ethereum processor lineage shadows an admitted completion helper.")]
    [TestCase("wrapper-override", "Standard Ethereum processor lineage shadows an admitted completion helper.")]
    public void Standard_processor_route_rejects_unsealed_or_overridden_completion(string mutation, string diagnostic)
    {
        using SourceFixture fixture = new();
        using TemporaryDirectory baseline = new();
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(baseline.Location, Path.Combine(baseline.Location, "baseline.lean")), Throws.Nothing);
        switch (mutation)
        {
            case "unsealed":
                fixture.ReplaceFirst(Extractor.TransactionProcessorPath,
                    "public sealed class EthereumTransactionProcessor(", "public class EthereumTransactionProcessor(");
                break;
            case "override":
                fixture.ReplaceFirst(Extractor.TransactionProcessorPath,
                    ": EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);",
                    ": EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel)\n    {\n        protected override void PayValue(Transaction tx, IReleaseSpec spec, ExecutionOptions opts) => base.PayValue(tx, spec, opts);\n    }");
                break;
            case "wrapper-override":
                fixture.ReplaceFirst(Extractor.TransactionProcessorPath,
                    ": TransactionProcessorBase<EthereumGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);",
                    ": TransactionProcessorBase<EthereumGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel)\n    {\n        protected override void PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, in TransactionSubstate substate, ulong spentGas, in UInt256 premiumPerGas, in UInt256 effectiveGasPrice, in UInt256 blobBaseFee, int statusCode) => base.PayFees(tx, header, spec, tracer, in substate, spentGas, in premiumPerGas, in effectiveGasPrice, in blobBaseFee, statusCode);\n    }");
                break;
            default:
                throw new AssertionException("Unknown processor-route mutation.");
        }
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(fixture.Output, Path.Combine(fixture.Output, "route.lean")),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
    }

    [Test]
    public void Explicit_interface_Process_bypass_is_compile_valid_but_rejected()
    {
        using SourceFixture fixture = new();
        using TemporaryDirectory baseline = new();
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(baseline.Location, Path.Combine(baseline.Location, "baseline.lean")), Throws.Nothing);
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath,
            ": EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);",
            ": EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel), ITransactionProcessor\n    {\n        TransactionResult ITransactionProcessor.Process(Transaction transaction, ITxTracer txTracer, ExecutionOptions options) => default;\n    }");
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(fixture.Output, Path.Combine(fixture.Output, "explicit-process.lean")),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                "Standard ITransactionProcessor.Process/3 no longer dispatches to the inherited generic Process source."));
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [TestCase("local", "LocalFunctionStatement")]
    [TestCase("lambda", "ParenthesizedLambdaExpression")]
    [TestCase("anonymous", "AnonymousMethodExpression")]
    public void Captured_restore_write_in_nested_caller_is_compile_valid_but_rejected(string mutation, string syntaxKind)
    {
        using SourceFixture fixture = new();
        using TemporaryDirectory baseline = new();
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(baseline.Location, Path.Combine(baseline.Location, "baseline.lean")), Throws.Nothing);
        string nested = mutation switch
        {
            "local" => "void ChangeRestore() => restore = !restore;\n            ChangeRestore();",
            "lambda" => "Action changeRestore = () => restore = !restore;\n            changeRestore();",
            "anonymous" => "Action changeRestore = delegate { restore = !restore; };\n            changeRestore();",
            _ => throw new AssertionException("Unknown nested-caller mutation."),
        };
        fixture.ReplaceFirst(Extractor.TransactionProcessorPath,
            "bool restore = opts.HasFlag(ExecutionOptions.Restore);",
            "bool restore = opts.HasFlag(ExecutionOptions.Restore);\n            " + nested);
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(fixture.Output, Path.Combine(fixture.Output, "nested-caller.lean")),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                "Nested callable " + syntaxKind + " in admitted caller member Execute."));
    }

    [TestCase("src/Nethermind/Nethermind.Evm/NoSuchSource.cs")]
    [TestCase("src/nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs")]
    [TestCase("src/Nethermind/Nethermind.Core/NoSuchProjection.cs")]
    [TestCase("src/nethermind/Nethermind.Core/GasCostOf.cs")]
    public void Unknown_or_miscased_override_keys_fail_before_source_splitting(string path)
    {
        Dictionary<string, string> overrides = new(StringComparer.Ordinal) { [path] = "" };
        Assert.That(() => Extractor.RequireSemanticCompilationForTest(FindRepoRoot(), overrides),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                "A source override is unknown, duplicated, or differs in case from the admitted compiler/projection roster: " + path));
    }

    [Test]
    public void Case_variant_duplicate_override_key_is_rejected_before_source_splitting()
    {
        string duplicate = "src/nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
        Dictionary<string, string> overrides = new(StringComparer.Ordinal)
        {
            [Extractor.TransactionProcessorPath] = "",
            [duplicate] = "",
        };
        Assert.That(() => Extractor.RequireSemanticCompilationForTest(FindRepoRoot(), overrides),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                "A source override is unknown, duplicated, or differs in case from the admitted compiler/projection roster: " + duplicate));
    }

    [Test]
    public void Exact_source_guards_and_max_accounting_fail_closed_when_mutated()
    {
        AssertUnmutatedBaselineExtracts();
        using SourceFixture buildUpFixture = new();
        buildUpFixture.Replace(Extractor.TransactionProcessorPath, "opts == ExecutionOptions.BuildUp", "opts.HasFlag(ExecutionOptions.BuildUp)");
        AssertCompileValidMutationRejected(buildUpFixture, "build-up", "Expected admitted condition/0 in FinalizeTransaction, found 0.");

        using SourceFixture maxFixture = new();
        maxFixture.Replace(Extractor.TransactionGasInitializationKernelPath, "Math.Max(blockExecutionGas, blockStateGas)", "blockExecutionGas + blockStateGas");
        AssertCompileValidMutationRejected(maxFixture, "max", "A closed typed source lowering for simple-transfer completion is incomplete.");

        using SourceFixture effectiveFixture = new();
        effectiveFixture.Replace(Extractor.GasConsumedPath, "BlockGas > 0 || BlockStateGas > 0 ? BlockGas : SpentGas", "BlockGas > 0 ? BlockGas : SpentGas");
        AssertCompileValidMutationRejected(effectiveFixture, "effective", "A closed typed source lowering for simple-transfer completion is incomplete.");
    }

    [TestCase("!isCodeOverridable && tx.AuthorizationList is null && !ForceSimpleTransferDisabled",
        "!isCodeOverridable && tx.AuthorizationList is null && ForceSimpleTransferDisabled",
        "Simple-transfer caller expression changed: IsSimpleTransferFastPathCandidate/!isCodeOverridable&&tx.AuthorizationList is null&&!ForceSimpleTransferDisabled.")]
    [TestCase("delegationAddress is null && codeInfo.IsEmpty",
        "delegationAddress is not null && codeInfo.IsEmpty",
        "Simple-transfer caller expression changed: HasNoExecutableCode/delegationAddress is null&&codeInfo.IsEmpty.")]
    [TestCase("followDelegation: !spec.IsEip8037Enabled",
        "followDelegation: spec.IsEip8037Enabled",
        "Simple-transfer caller expression changed: PrepareSimpleTransferFastPath/_codeInfoRepository.GetCachedCodeInfo(recipient,followDelegation:!spec.IsEip8037Enabled,spec,out preloadedDelegationAddress).")]
    [TestCase("commit && (simpleTransferRecipient is null || restore || tracer.IsTracingState)",
        "commit && (simpleTransferRecipient is null || restore)",
        "Simple-transfer caller expression changed: Execute/commit&&(simpleTransferRecipient is null||restore||tracer.IsTracingState).")]
    [TestCase("simpleTransferRecipient is not null", "simpleTransferRecipient is null",
        "Simple-transfer caller expression changed: Execute/simpleTransferRecipient is not null.")]
    [TestCase("in senderReservedGasPayment, in blobBaseFee);",
        "in senderReservedGasPayment, in senderReservedGasPayment);",
        "Simple-transfer caller expression changed: Execute/ExecuteSimpleTransfer(tx,header,spec,tracer,opts,restore,commit,deleteCallerAccount,simpleTransferRecipient,in intrinsicGas,gasAvailable,in opcodeGasPrice,in premiumPerGas,in senderReservedGasPayment,in blobBaseFee).")]
    public void Caller_route_mutations_fail_closed(string original, string replacement, string diagnostic)
    {
        using SourceFixture fixture = new();
        using TemporaryDirectory baseline = new();
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(baseline.Location,
            Path.Combine(baseline.Location, "baseline.lean")), Throws.Nothing);
        fixture.Replace(Extractor.TransactionProcessorPath, original, replacement);
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(fixture.Output,
            Path.Combine(fixture.Output, "caller.lean")),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [TestCase(
        "Address? simpleTransferRecipient = PrepareSimpleTransferFastPath(tx, spec, out CodeInfo? preloadedCodeInfo, out Address? preloadedDelegationAddress);",
        "Address? simpleTransferRecipient = PrepareSimpleTransferFastPath(tx, spec, out CodeInfo? preloadedCodeInfo, out Address? preloadedDelegationAddress);\n            simpleTransferRecipient = tx.To;",
        "Caller local has a second reaching definition or an escaping alias: simpleTransferRecipient")]
    [TestCase(
        "preloadedCodeInfo = _codeInfoRepository.GetCachedCodeInfo(recipient, followDelegation: !spec.IsEip8037Enabled, spec, out preloadedDelegationAddress);",
        "if (false) preloadedCodeInfo = _codeInfoRepository.GetCachedCodeInfo(recipient, followDelegation: !spec.IsEip8037Enabled, spec, out preloadedDelegationAddress);",
        "Simple-transfer preparation has an unmodeled return, path, or effect.")]
    [TestCase(
        "bool commitBeforeExecution = commit && (simpleTransferRecipient is null || restore || tracer.IsTracingState);",
        "bool commitBeforeExecution = commit && (simpleTransferRecipient is null || restore || tracer.IsTracingState);\n            commitBeforeExecution = true;",
        "Caller local has a second reaching definition or an escaping alias: commitBeforeExecution")]
    [TestCase(
        "bool commitBeforeExecution = commit && (simpleTransferRecipient is null || restore || tracer.IsTracingState);",
        "bool commitBeforeExecution = commit && (simpleTransferRecipient is null || restore || tracer.IsTracingState) || true;",
        "Caller local definition changed: commitBeforeExecution")]
    [TestCase(
        "=> !isCodeOverridable && tx.AuthorizationList is null && !ForceSimpleTransferDisabled;",
        "=> !isCodeOverridable && tx.AuthorizationList is null && !ForceSimpleTransferDisabled || true;",
        "Simple-transfer candidate or no-code predicate is not the complete returned expression.")]
    [TestCase(
        "=> delegationAddress is null && codeInfo.IsEmpty;",
        "=> delegationAddress is null && codeInfo.IsEmpty || true;",
        "Simple-transfer candidate or no-code predicate is not the complete returned expression.")]
    [TestCase(
        "return HasNoExecutableCode(preloadedCodeInfo, preloadedDelegationAddress) ? recipient : null;",
        "return (HasNoExecutableCode(preloadedCodeInfo, preloadedDelegationAddress) ? recipient : null) ?? recipient;",
        "Simple-transfer preparation has an unmodeled return, path, or effect.")]
    [TestCase(
        "Address? recipient = tx.To;",
        "Address? recipient = tx.To;\n            if (recipient is not null) return null;",
        "Simple-transfer preparation has an unmodeled return, path, or effect.")]
    [TestCase(
        "preloadedCodeInfo = _codeInfoRepository.GetCachedCodeInfo(recipient, followDelegation: !spec.IsEip8037Enabled, spec, out preloadedDelegationAddress);",
        "preloadedCodeInfo = _codeInfoRepository.GetCachedCodeInfo(recipient, followDelegation: !spec.IsEip8037Enabled, spec, out preloadedDelegationAddress);\n            if (preloadedCodeInfo.IsEmpty) return null;",
        "Simple-transfer preparation has an unmodeled return, path, or effect.")]
    [TestCase(
        "preloadedCodeInfo = _codeInfoRepository.GetCachedCodeInfo(recipient, followDelegation: !spec.IsEip8037Enabled, spec, out preloadedDelegationAddress);",
        "preloadedCodeInfo = _codeInfoRepository.GetCachedCodeInfo(recipient, followDelegation: !spec.IsEip8037Enabled, spec, out preloadedDelegationAddress);\n            preloadedDelegationAddress = null;",
        "Simple-transfer preparation has an unmodeled return, path, or effect.")]
    public void Compilable_caller_reaching_definition_mutations_fail_closed(string original, string replacement, string diagnostic)
    {
        using SourceFixture fixture = new();
        using TemporaryDirectory baseline = new();
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(baseline.Location,
            Path.Combine(baseline.Location, "baseline.lean")), Throws.Nothing);
        fixture.Replace(Extractor.TransactionProcessorPath, original, replacement);
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(fixture.Output,
            Path.Combine(fixture.Output, "caller-def.lean")),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [Test]
    public void Compilable_while_mutation_has_named_syntax_diagnostic()
    {
        using SourceFixture fixture = new();
        using TemporaryDirectory baseline = new();
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(baseline.Location,
            Path.Combine(baseline.Location, "baseline.lean")), Throws.Nothing);
        fixture.Replace(Extractor.TransactionProcessorPath, "Metrics.IncrementEmptyCalls();",
            "Metrics.IncrementEmptyCalls();\n            while (opts.HasFlag(ExecutionOptions.Commit)) break;");
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(fixture.Output,
            Path.Combine(fixture.Output, "while.lean")),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(
                "Unsupported syntax WhileStatement in admitted completion member ExecuteSimpleTransfer."));
    }

    [Test]
    public void Interrupted_publication_is_detected_and_can_be_recovered()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory baseline = new();
        string leanPath = Path.Combine(baseline.Location, "SimpleTransferCompletion.lean");
        ExtractionResult result = Extractor.Extract(root, baseline.Location, leanPath);
        byte[] originalIr = File.ReadAllBytes(result.IrPath);
        byte[] originalLean = File.ReadAllBytes(result.LeanPath);
        byte[] originalManifest = File.ReadAllBytes(result.ManifestPath);
        byte[] nextIr = (byte[])originalIr.Clone();
        nextIr[^1] ^= 0x01;

        Assert.That(() => Extractor.PublishArtifactsForTest(root, baseline.Location, leanPath,
            nextIr, originalLean, originalManifest, () => throw new IOException("injected publication interruption")),
            Throws.TypeOf<IOException>());
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(result.IrPath), Is.EqualTo(nextIr));
            Assert.That(File.ReadAllBytes(result.LeanPath), Is.EqualTo(originalLean));
            Assert.That(File.ReadAllBytes(result.ManifestPath), Is.EqualTo(originalManifest));
        });
        Assert.That(() => Extractor.ValidateExistingArtifacts(root, baseline.Location, leanPath),
            Throws.TypeOf<ExtractionException>().With.Message.StartWith("Generated simple-transfer IR drifted:"));

        Extractor.PublishArtifactsForTest(root, baseline.Location, leanPath,
            originalIr, originalLean, originalManifest);
        Assert.That(() => Extractor.ValidateExistingArtifacts(root, baseline.Location, leanPath), Throws.Nothing);
    }

    [Test]
    public void Access_warmup_source_mutations_fail_closed()
    {
        AssertUnmutatedBaselineExtracts();
        using SourceFixture fixture = new();
        fixture.Replace(Extractor.TransactionProcessorPath, "if (spec.UseTxAccessLists)", "if (false && spec.UseTxAccessLists)");
        AssertCompileValidMutationRejected(fixture, "access", "WarmUpTxAccesses lost a typed access-list, coinbase, or recipient guard.");

        using SourceFixture orderFixture = new();
        orderFixture.Replace(
            Extractor.TransactionProcessorPath,
            "if (warmUpRecipient)\n                accessTracker.WarmUp(recipient);\n            accessTracker.WarmUp(tx.SenderAddress!);",
            "if (warmUpRecipient)\n                accessTracker.WarmUp(tx.SenderAddress!);\n            accessTracker.WarmUp(recipient);");
        AssertCompileValidMutationRejected(orderFixture, "access-order", "WarmUpTxAccesses changed source order, overload, receiver, or argument 2.");

    }

    [Test]
    public void Source_lowered_guard_cost_and_no_frame_mutations_cannot_hide()
    {
        AssertUnmutatedBaselineExtracts();
        using SourceFixture guardFixture = new();
        guardFixture.Replace(Extractor.TransactionProcessorPath, "spec.IsEip8037Enabled && hasValueTransfer", "spec.IsEip8037Enabled && !hasValueTransfer");
        Assert.That(() => guardFixture.RequireSemanticCompilation(), Throws.Nothing);
        using TemporaryDirectory baselineDirectory = new();
        ExtractionResult baseline = Extractor.Extract(FindRepoRoot(), baselineDirectory.Location, Path.Combine(baselineDirectory.Location, "baseline.lean"));
        ExtractionResult guard = guardFixture.Extract(guardFixture.Output, Path.Combine(guardFixture.Output, "guard.lean"));
        Assert.That(File.ReadAllBytes(guard.LeanPath), Is.Not.EqualTo(File.ReadAllBytes(baseline.LeanPath)));
        Assert.That(File.ReadAllText(guard.LeanPath), Does.Contain(".negate (.hasValue)"));

        using SourceFixture deadRecipientFixture = new();
        deadRecipientFixture.Replace(Extractor.TransactionProcessorPath, "WorldState.IsDeadAccount(recipient)", "!WorldState.IsDeadAccount(recipient)");
        Assert.That(() => deadRecipientFixture.RequireSemanticCompilation(), Throws.Nothing);
        ExtractionResult deadRecipient = deadRecipientFixture.Extract(
            deadRecipientFixture.Output, Path.Combine(deadRecipientFixture.Output, "dead-recipient.lean"));
        Assert.That(File.ReadAllText(deadRecipient.LeanPath), Does.Contain(".negate (.recipientDead)"));

        using SourceFixture costFixture = new();
        costFixture.Replace(Extractor.GasCostOfPath, "NewAccountState = StateBytesPerNewAccount * CostPerStateByte", "NewAccountState = 1");
        AssertCompileValidMutationRejected(costFixture, "cost", "GasCostOf.NewAccountState source formula and accepted Core metadata disagree.");

        using SourceFixture policyCostFixture = new();
        policyCostFixture.Replace(Extractor.GasPolicyInterfacePath, "GasCostOf.NewAccountState", "1");
        AssertCompileValidMutationRejected(policyCostFixture, "policy-cost",
            "IGasPolicy.GetNewAccountStateCost no longer returns the exact GasCostOf.NewAccountState schedule field.");

        using SourceFixture noFrameFixture = new();
        noFrameFixture.Replace(Extractor.TransactionProcessorPath, "goto CompleteWithoutFrame;", "goto Complete;");
        AssertCompileValidMutationRejected(noFrameFixture, "no-frame", "ExecuteEvmCall must retain exactly four no-frame gotos, found 0.");

        using SourceFixture logFixture = new();
        logFixture.Replace(Extractor.TransferLogPath,
            "[TransferSignature, from.ToHash().ToHash256(), to.ToHash().ToHash256()]",
            "[TransferSignature, to.ToHash().ToHash256(), from.ToHash().ToHash256()]");
        AssertCompileValidMutationRejected(logFixture, "log-payload",
            "TransferLog.CreateTransferInternal changed the Transfer topic/address/hash payload order.");

        using SourceFixture dataFixture = new();
        dataFixture.Replace(Extractor.TransferLogPath, "amount.ToBigEndian()", "amount.ToBigEndian().Reverse().ToArray()");
        AssertCompileInvalidMutationRejected(dataFixture, "log-data", "CS1501");

        using SourceFixture counterFixture = new();
        counterFixture.Replace(
            Extractor.TransactionProcessorPath,
            "_blockCumulativeExecutionGas += spentGas.EffectiveBlockGas;",
            "_blockCumulativeExecutionGas += spentGas.BlockGas;");
        AssertCompileValidMutationRejected(counterFixture, "counter-payload",
            "UpdateHeaderGasUsedAndPayFees EIP-8037 cumulative counter guard or true-arm effects changed.");

        using SourceFixture receiptFixture = new();
        receiptFixture.Replace(
            Extractor.TransactionProcessorPath,
            "WorldState.RecalculateStateRoot();\n                    stateRoot = WorldState.StateRoot;",
            "stateRoot = WorldState.StateRoot;");
        AssertCompileValidMutationRejected(receiptFixture, "receipt-root",
            "FinalizeTransaction lost the typed receipt-root or receipt-continuation operations.");

        using SourceFixture payValueOrderFixture = new();
        payValueOrderFixture.Replace(
            Extractor.TransactionProcessorPath,
            "if (hasValueTransfer) PayValue(tx, spec, opts);\n                WorldState.AddToBalanceAndCreateIfNotExists(recipient, in hasValueTransfer ? ref value : ref UInt256.Zero, spec);",
            "WorldState.AddToBalanceAndCreateIfNotExists(recipient, in hasValueTransfer ? ref value : ref UInt256.Zero, spec);\n                if (hasValueTransfer) PayValue(tx, spec, opts);");
        AssertCompileValidMutationRejected(payValueOrderFixture, "pay-value-order",
            "Simple-transfer source order changed before 'InvocationExpression'.");

        using SourceFixture predicateArgumentFixture = new();
        predicateArgumentFixture.Replace(
            Extractor.TransactionProcessorPath,
            "WorldState.IsDeadAccount(recipient)",
            "WorldState.IsDeadAccount(tx.SenderAddress!)");
        AssertCompileValidMutationRejected(predicateArgumentFixture, "predicate-argument",
            "IsDeadAccount predicate changed its exact WorldState/recipient argument grammar.");

        using SourceFixture counterArgumentFixture = new();
        counterArgumentFixture.Replace(
            Extractor.TransactionProcessorPath,
            "SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters(opts, _parallel)",
            "SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters(opts, false)");
        AssertCompileValidMutationRejected(counterArgumentFixture, "counter-argument",
            "ParticipatesInNormalBlockCounters predicate changed its exact options/parallel grammar.");
    }

    [Test]
    public void Typed_invocation_grammar_rejects_argument_receiver_and_overload_mutations()
    {
        AssertUnmutatedBaselineExtracts();
        using SourceFixture argumentFixture = new();
        argumentFixture.Replace(
            Extractor.TransactionProcessorPath,
            "TraceSimpleTransferActionStart(tx, recipient, tracer, in value, in gasAvailable)",
            "TraceSimpleTransferActionStart(tx, recipient, tracer, in gasAvailable, in value)");
        AssertCompileInvalidMutationRejected(argumentFixture, "argument", "CS1503");

        using SourceFixture sameTypeArgumentFixture = new();
        sameTypeArgumentFixture.Replace(
            Extractor.TransactionProcessorPath,
            "opts, restore, commit, deleteCallerAccount",
            "opts, commit, restore, deleteCallerAccount");
        AssertCompileValidMutationRejected(sameTypeArgumentFixture, "same-type-argument",
            "Simple-transfer caller expression changed: Execute/ExecuteSimpleTransfer(tx,header,spec,tracer,opts,restore,commit,deleteCallerAccount,simpleTransferRecipient,in intrinsicGas,gasAvailable,in opcodeGasPrice,in premiumPerGas,in senderReservedGasPayment,in blobBaseFee).");

        using SourceFixture receiverFixture = new();
        receiverFixture.Replace(
            Extractor.TransactionProcessorPath,
            "WorldState.AddToBalanceAndCreateIfNotExists(recipient, in hasValueTransfer ? ref value : ref UInt256.Zero, spec)",
            "WorldStateExtensions.AddToBalanceAndCreateIfNotExists(WorldState, recipient, in hasValueTransfer ? ref value : ref UInt256.Zero, spec)");
        AssertCompileValidMutationRejected(receiverFixture, "receiver",
            "Typed operation addRecipientRequest changed its source receiver syntax: WorldStateExtensions.AddToBalanceAndCreateIfNotExists(WorldState,recipient,inhasValueTransfer?refvalue:refUInt256.Zero,spec).");

        using SourceFixture overloadFixture = new();
        overloadFixture.Replace(
            Extractor.TransactionProcessorPath,
            "accessTracker.WarmUp(tx.AccessList)",
            "accessTracker.WarmUp(recipient)");
        AssertCompileValidMutationRejected(overloadFixture, "overload",
            "WarmUpTxAccesses changed source order, overload, receiver, or argument 0.");
    }

    [Test]
    public void Semantic_closure_rejects_unresolved_source_symbols()
    {
        AssertUnmutatedBaselineExtracts();
        using SourceFixture fixture = new();
        fixture.Replace(Extractor.TransactionProcessorPath, "bool hasValueTransfer = !value.IsZero;", "bool hasValueTransfer = !value.NoSuchMember;");
        AssertCompileInvalidMutationRejected(fixture, "unresolved", "CS1061");

        using SourceFixture ambiguousFixture = new();
        ambiguousFixture.Replace(Extractor.TransactionProcessorPath, "accessTracker.WarmUp(tx.AccessList)", "accessTracker.WarmUp(default)");
        AssertCompileInvalidMutationRejected(ambiguousFixture, "ambiguous", "CS0121");
    }

    [Test]
    public void Reference_and_refinement_are_independent_files()
    {
        string root = FindRepoRoot();
        string package = Path.Combine(root, "tools", "Evm", "Lean", "SimpleTransferCompletionExtractor");
        string reference = File.ReadAllText(Path.Combine(package, "Reference", "SimpleTransferCompletionReference.lean"));
        string refinement = File.ReadAllText(Path.Combine(package, "Refinement", "SimpleTransferCompletionRefinement.lean"));
        Assert.Multiple(() =>
        {
            Assert.That(reference, Does.Contain("structure Case"));
            Assert.That(reference, Does.Contain("def accessObservation"));
            Assert.That(reference, Does.Contain("def run (case : Case)"));
            Assert.That(reference, Does.Not.Contain("Generated.SimpleTransferCompletion"));
            Assert.That(reference, Does.Not.Contain("OrdinaryPostNonceDispatchExtractor"));
            Assert.That(reference, Does.Not.Contain("transferTopicOracle"));
            Assert.That(reference, Does.Not.Contain("theorem universal_refinement"));
            Assert.That(refinement, Does.Contain("AdapterCoherent"));
            Assert.That(refinement, Does.Contain("uint256Max"));
            Assert.That(refinement, Does.Contain("allUInt64"));
            Assert.That(refinement, Does.Contain("allInt64"));
            Assert.That(refinement, Does.Contain("theorem universal_refinement"));
            Assert.That(refinement, Does.Not.Contain("source_identity_bridge"));
            Assert.That(refinement, Does.Contain("receipt_continuation_preserved"));
            Assert.That(refinement, Does.Contain("mapWorldRequest"));
            Assert.That(refinement, Does.Contain("accessObservationExtensional"));
            Assert.That(refinement, Does.Contain("journalExtensional"));
            Assert.That(refinement, Does.Contain("Generated.addressHashProjection"));
            Assert.That(refinement, Does.Contain("transfer_topic_projection_bridge"));
            Assert.That(refinement, Does.Contain("normalReturnDomain"));
            Assert.That(refinement, Does.Contain("normal_return_domain_excludes_callback_prefix"));
            Assert.That(refinement, Does.Not.Contain("TransferPayloadAdapter"));
            Assert.That(refinement, Does.Not.Contain("transferPayload"));
            Assert.That(refinement, Does.Not.Contain("transferTopicOracle"));
            Assert.That(refinement, Does.Not.Contain("transferLogOracle"));
        });
    }

    [Test]
    public void Mutated_live_lean_typechecks_with_warnings_as_errors()
    {
        string root = FindRepoRoot();
        using SourceFixture fixture = new();
        fixture.Replace(Extractor.TransactionProcessorPath, "!senderIsRecipient", "senderIsRecipient");
        ExtractionResult result = fixture.Extract(fixture.Output, Path.Combine(fixture.Output, "mutated.lean"));
        AssertLeanCompiles(Path.Combine(root, "tools", "Evm", "Lean"), result.LeanPath);
    }

    private static void AssertLeanCompiles(string leanWorkspace, string leanPath)
    {
        ProcessStartInfo info = new("lake", $"env lean -DwarningAsError=true -DmaxHeartbeats=800000 \"{leanPath}\"")
        {
            WorkingDirectory = leanWorkspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process process = Process.Start(info) ?? throw new AssertionException("Could not start Lake.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.EqualTo(0), output);
    }

    private static void AssertUnmutatedBaselineExtracts()
    {
        using SourceFixture baseline = new();
        using TemporaryDirectory output = new();
        Assert.That(() => baseline.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => baseline.Extract(output.Location, Path.Combine(output.Location, "baseline.lean")), Throws.Nothing);
    }

    private static void AssertCompileValidMutationRejected(SourceFixture fixture, string name, string diagnostic)
    {
        Assert.That(() => fixture.RequireSemanticCompilation(), Throws.Nothing);
        Assert.That(() => fixture.Extract(fixture.Output, Path.Combine(fixture.Output, name + ".lean")),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(diagnostic));
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    private static void AssertCompileInvalidMutationRejected(SourceFixture fixture, string name, string compilerCode)
    {
        string diagnostic = "error " + compilerCode + ":";
        Assert.That(() => fixture.RequireSemanticCompilation(),
            Throws.TypeOf<ExtractionException>().With.Message.Contains(diagnostic));
        Assert.That(() => fixture.Extract(fixture.Output, Path.Combine(fixture.Output, name + ".lean")),
            Throws.TypeOf<ExtractionException>().With.Message.Contains(diagnostic));
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new AssertionException("Could not locate repository root.");
    }

    private static byte[] SerializeJson(JsonNode value) => Encoding.UTF8.GetBytes(value.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true,
        NewLine = "\n",
    }) + "\n");

    private static (byte[] Ir, byte[] Manifest) FreshSerializedCandidate()
    {
        using TemporaryDirectory candidate = new();
        ExtractionResult result = Extractor.Extract(FindRepoRoot(), candidate.Location,
            Path.Combine(candidate.Location, "SimpleTransferCompletion.lean"));
        return (File.ReadAllBytes(result.IrPath), File.ReadAllBytes(result.ManifestPath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Location = Path.Combine(Path.GetTempPath(), "simple-transfer-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Location);
        }

        internal string Location { get; }
        public void Dispose() => Directory.Delete(Location, recursive: true);
    }

    private sealed class SourceFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);

        internal SourceFixture()
        {
            Root = FindRepoRoot();
            Output = _directory.Location;
        }

        internal string Root { get; }
        internal string Output { get; }

        internal void Replace(string relativePath, string original, string replacement)
        {
            string source = _overrides.TryGetValue(relativePath, out string? existing)
                ? existing : File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!source.Contains(original, StringComparison.Ordinal))
                throw new AssertionException($"Mutation anchor not found: {original}");
            _overrides[relativePath] = source.Replace(original, replacement, StringComparison.Ordinal);
        }

        internal void ReplaceFirst(string relativePath, string original, string replacement)
        {
            string source = _overrides.TryGetValue(relativePath, out string? existing)
                ? existing : File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            int anchor = source.IndexOf(original, StringComparison.Ordinal);
            if (anchor < 0) throw new AssertionException($"Mutation anchor not found: {original}");
            _overrides[relativePath] = source[..anchor] + replacement + source[(anchor + original.Length)..];
        }

        internal void Append(string relativePath, string suffix)
        {
            string source = _overrides.TryGetValue(relativePath, out string? existing)
                ? existing : File.ReadAllText(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            _overrides[relativePath] = source + suffix;
        }

        internal ExtractionResult Extract(string output, string leanPath) =>
            Extractor.ExtractWithoutPinnedSourcesForTest(Root, output, leanPath, _overrides);

        internal ExtractionResult ExtractPinned(string output, string leanPath) =>
            Extractor.ExtractPinnedSourcesForTest(Root, output, leanPath, _overrides);

        internal void RequireSemanticCompilation() =>
            Extractor.RequireSemanticCompilationForTest(Root, _overrides);

        internal StageASourcePlan RequireStageASourcePlan() =>
            Extractor.RequireStageASourcePlanForTest(Root, _overrides);

        internal StageBPlan RequireStageBPlan() => StageBLowering.Build(Root, _overrides);

        internal void RequireStageBCompilation() => _ = CompilerSources.Load(Root, _overrides);

        public void Dispose() => _directory.Dispose();
    }
}

[TestFixture]
public sealed class StageBRefundDispatchTests
{
    private const string SimpleRefundCall = "GasConsumed spentGas = Refund(tx, header, spec, opts, in substate, in gasAvailable, in opcodeGasPrice, codeInsertRefunds: 0, in floorGas, in standardGas, postIntrinsicStateReservoir);";
    private const string SettlementPayRefundCall = "PayRefund(tx, (tx.GasLimit - settlement.SpentGas) * gasPrice, spec);";
    private const string GenericLeafTail = """
                : TransactionProcessorBase<TGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel)
                where TGasPolicy : struct, IGasPolicy<TGasPolicy>
            {
            }
        """;

    private const string EthereumLeafTail = """
            : EthereumTransactionProcessorBase(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);
        """;

    private string _root = null!;
    private CompilerClosure _closure = null!;

    [OneTimeSetUp]
    public void Load()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, StageBRefundDispatchExtractor.ProductionPath)))
            directory = directory.Parent;
        _root = directory?.FullName ?? throw new AssertionException("Repository missing.");
        _closure = CompilerSources.Load(_root);
    }

    [Test]
    public void Stage_b_refund_dispatch_admits_both_standard_receiver_leaves_and_calls()
    {
        StageBRefundDispatchDocument document = StageBRefundDispatchExtractor.Generate(_root, _closure);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.Receivers.Select(static receiver => receiver.Kind),
                Is.EqualTo(new[] { "ethereumTransactionProcessor", "balTransactionProcessor" }));
            Assert.That(document.Receivers, Has.All.Property(nameof(StageBRefundDispatchReceiver.IsSealed)).True);
            Assert.That(document.Receivers, Has.All.Property(nameof(StageBRefundDispatchReceiver.DeclaresRefund)).False);
            Assert.That(document.Receivers, Has.All.Property(nameof(StageBRefundDispatchReceiver.DeclaresPayRefund)).False);
            Assert.That(document.Receivers.Select(static receiver => receiver.RefundDeclaringType), Has.All.EqualTo(document.BaseProcessor));
            Assert.That(document.Receivers.Select(static receiver => receiver.PayRefundDeclaringType), Has.All.EqualTo(document.BaseProcessor));
            Assert.That(document.Calls.Select(static call => call.Role),
                Is.EqualTo(new[] { "executeSimpleTransferRefund", "refundPayRefund" }));
            Assert.That(document.Calls, Has.All.Property(nameof(StageBRefundDispatchCall.Receiver)).EqualTo("containingInstance"));
            Assert.That(document.Upstreams.Select(static upstream => upstream.Name),
                Is.EqualTo(new[] { "stageBPrefix", "ordinaryRefund" }));
            Assert.That(document.Exclusions, Is.EqualTo(new[]
            {
                "autofac-resolution",
                "plugin-module-selection",
                "host-execution",
                "refund-return",
                "caller-result-assignment",
                "pay-refund-world-state-effect",
                "stage-b-resume",
                "system-transaction-processor",
                "optimism-transaction-processor",
                "taiko-transaction-processor",
            }));
        }
    }

    [TestCase("generic", "Refund")]
    [TestCase("generic", "PayRefund")]
    [TestCase("ethereum", "Refund")]
    [TestCase("ethereum", "PayRefund")]
    public void Stage_b_refund_dispatch_rejects_a_standard_leaf_refund_slot_override(string leaf, string slot)
    {
        string gasPolicy = leaf == "generic" ? "TGasPolicy" : "EthereumGasPolicy";
        string method = slot switch
        {
            "Refund" => $"protected override GasConsumed Refund(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts, in TransactionSubstate substate, in {gasPolicy} unspentGas, in UInt256 gasPrice, ulong codeInsertRefunds, in {gasPolicy} floorGas, in {gasPolicy} intrinsicGasStandard, long postIntrinsicStateReservoir, bool topLevelCreateStateGasCharged = false) => default;",
            "PayRefund" => "protected override void PayRefund(Transaction tx, UInt256 refundAmount, IReleaseSpec spec) { }",
            _ => throw new AssertionException("Unknown refund slot."),
        };
        CompilerClosure mutant = leaf switch
        {
            "generic" => Mutate(GenericLeafTail, GenericLeafTail.Replace("    {\n    }", "    {\n        " + method + "\n    }", StringComparison.Ordinal)),
            "ethereum" => Mutate(EthereumLeafTail, EthereumLeafTail.Replace("parallel);", "parallel)\n    {\n        " + method + "\n    }", StringComparison.Ordinal)),
            _ => throw new AssertionException("Unknown receiver leaf."),
        };
        Assert.That(() => StageBRefundDispatchExtractor.Generate(_root, mutant),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("shadows Refund or PayRefund"));
    }

    [TestCase("refund")]
    [TestCase("payRefund")]
    public void Stage_b_refund_dispatch_rejects_a_non_containing_instance_call(string mutation)
    {
        (string original, string replacement, string role) = mutation switch
        {
            "refund" => (SimpleRefundCall,
                SimpleRefundCall.Replace("= Refund(", "= ((TransactionProcessorBase<TGasPolicy>)this).Refund(", StringComparison.Ordinal),
                "executeSimpleTransferRefund"),
            "payRefund" => (SettlementPayRefundCall,
                SettlementPayRefundCall.Replace("PayRefund(", "((TransactionProcessorBase<TGasPolicy>)this).PayRefund(", StringComparison.Ordinal),
                "refundPayRefund"),
            _ => throw new AssertionException("Unknown call mutation."),
        };
        CompilerClosure mutant = Mutate(original, replacement);
        Assert.That(() => StageBRefundDispatchExtractor.Generate(_root, mutant),
            Throws.TypeOf<ExtractionException>().With.Message.EqualTo(role + " must use the containing instance receiver."));
    }

    [TestCase("unseal-generic")]
    [TestCase("concretize-wrapper")]
    [TestCase("nonvirtual-refund")]
    [TestCase("remove-refund-call")]
    [TestCase("duplicate-pay-refund")]
    public void Stage_b_refund_dispatch_rejects_lineage_slot_and_call_drift(string mutation)
    {
        (string original, string replacement) = mutation switch
        {
            "unseal-generic" => ("public sealed class TransactionProcessor<TGasPolicy>(",
                "public class TransactionProcessor<TGasPolicy>("),
            "concretize-wrapper" => ("public abstract class EthereumTransactionProcessorBase(",
                "public class EthereumTransactionProcessorBase("),
            "nonvirtual-refund" => ("protected virtual GasConsumed Refund(",
                "protected GasConsumed Refund("),
            "remove-refund-call" => (SimpleRefundCall, "GasConsumed spentGas = default;"),
            "duplicate-pay-refund" => (SettlementPayRefundCall,
                SettlementPayRefundCall + "\n            " + SettlementPayRefundCall),
            _ => throw new AssertionException("Unknown dispatch mutation."),
        };
        CompilerClosure mutant = Mutate(original, replacement);
        Assert.That(() => StageBRefundDispatchExtractor.Generate(_root, mutant),
            Throws.TypeOf<ExtractionException>());
    }

    [TestCase("corrupt-ir")]
    [TestCase("corrupt-lean")]
    [TestCase("corrupt-manifest")]
    [TestCase("missing")]
    [TestCase("extra")]
    public void Stage_b_refund_dispatch_artifacts_are_deterministic_and_fail_closed(string mutation)
    {
        Dictionary<string, byte[]> first = StageBRefundDispatchExtractor.RenderArtifacts(_root, _closure);
        Dictionary<string, byte[]> second = StageBRefundDispatchExtractor.RenderArtifacts(_root, _closure);
        Assert.That(first.Keys.Order(), Is.EqualTo(second.Keys.Order()));
        foreach (string name in first.Keys) Assert.That(first[name], Is.EqualTo(second[name]), name);

        string directory = Path.Combine(_root, StageBRefundDispatchExtractor.PackagePath, ".lake", "dispatch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach ((string name, byte[] bytes) in first) File.WriteAllBytes(Path.Combine(directory, name), bytes);
            Assert.That(() => StageBRefundDispatchExtractor.ValidateDirectory(directory, second), Throws.Nothing);
            string? changedName = mutation switch
            {
                "corrupt-ir" => StageBRefundDispatchExtractor.IrName,
                "corrupt-lean" => StageBRefundDispatchExtractor.LeanName,
                "corrupt-manifest" => StageBRefundDispatchExtractor.ManifestName,
                "missing" or "extra" => null,
                _ => throw new AssertionException("Unknown artifact mutation."),
            };
            if (changedName is not null)
            {
                string changedPath = Path.Combine(directory, changedName);
                byte[] changed = File.ReadAllBytes(changedPath);
                changed[^2] ^= 1;
                File.WriteAllBytes(changedPath, changed);
            }
            else if (mutation == "missing")
            {
                File.Delete(Path.Combine(directory, StageBRefundDispatchExtractor.IrName));
            }
            else
            {
                File.WriteAllBytes(Path.Combine(directory, "unexpected.txt"), []);
            }

            Assert.That(() => StageBRefundDispatchExtractor.ValidateDirectory(directory, second),
                Throws.TypeOf<ExtractionException>().With.Message.Contains(
                    changedName ?? "Refund-dispatch artifact roster changed."));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private CompilerClosure Mutate(string original, string replacement)
    {
        SyntaxTree tree = _closure.Compilation.SyntaxTrees.Single(static tree =>
            tree.FilePath.Replace('\\', '/') == StageBRefundDispatchExtractor.ProductionPath);
        string source = tree.GetText().ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.That(source.Split(original, StringSplitOptions.None), Has.Length.EqualTo(2), "Mutation target must be unique.");
        SyntaxTree changed = CSharpSyntaxTree.ParseText(source.Replace(original, replacement, StringComparison.Ordinal),
            (CSharpParseOptions)tree.Options, tree.FilePath);
        CSharpCompilation compilation = _closure.Compilation.ReplaceSyntaxTree(tree, changed);
        Diagnostic[] errors = compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(errors, Is.Empty, string.Join('\n', errors.Select(static error => error.ToString())));
        return _closure with { Compilation = compilation };
    }
}

[TestFixture]
public sealed class StageBControlTests
{
    private string _root = null!;
    private StageBControlCompilation _inputs = null!;

    [OneTimeSetUp]
    public void Load()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, StageBControlExtractor.KernelPath))) directory = directory.Parent;
        _root = directory?.FullName ?? throw new AssertionException("Repository missing.");
        _inputs = StageBControlExtractor.Load(_root);
    }

    [TestCase(0L, true, 0L)]
    [TestCase(1L, false, 0L)]
    [TestCase(855L, false, 854L)]
    [TestCase(long.MaxValue, false, long.MaxValue - 1)]
    [TestCase(long.MinValue, false, long.MaxValue)]
    public void Stage_b_control_tick_preserves_candidate_decision_order(long fuel, bool exhausted, long remaining) =>
        Assert.That(StageBControlKernel.Tick(fuel), Is.EqualTo(new StageBTickResult(exhausted, remaining)));

    [TestCase(false, false, 0)]
    [TestCase(false, true, 0)]
    [TestCase(true, false, 2)]
    [TestCase(true, true, 1)]
    public void Stage_b_control_local_return_matches_independent_table(bool constructing, bool valueMode, int expected) =>
        Assert.That((int)StageBControlKernel.SelectLocalReturn(constructing, valueMode), Is.EqualTo(expected));

    [TestCase(false, false, false)]
    [TestCase(false, true, true)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    public void Stage_b_control_transparent_read_matches_independent_table(bool preservesOperand, bool valueMode, bool expected) =>
        Assert.That(StageBControlKernel.ShouldReadTransparent(preservesOperand, valueMode), Is.EqualTo(expected));

    [Test]
    public void Stage_b_control_local_return_adapter_reads_only_value_constructor_receiver(
        [Values] bool constructing, [Values(0, 1, 2)] int mode)
    {
        Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax call = _inputs.Compilation.GetTypeByMetadataName(
            "Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBPrefixInterpreter+Machine")!.GetMembers("Call")
            .Single().DeclaringSyntaxReferences.Single().GetSyntax() as Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax
            ?? throw new AssertionException("Call is missing.");
        string selection = string.Join('\n', call.Body!.Statements.TakeLast(3));
        int[] expected = constructing && mode == 0 ? [1, 0, -1] : constructing ? [0, 1, -1] : [0, 0, 71];
        Assert.That(ExecuteSelection(selection), Is.EqualTo(expected), "Actual source return tail");
        if (!constructing || mode != 0)
            Assert.That(ExecuteSelection("_ = receiver.Read();\n" + selection), Is.Not.EqualTo(expected), "Eager-read fault must fail the same expected observation");

        int[] ExecuteSelection(string body)
        {
            string probe = $$"""
                internal static int[] ProbeLocalReturn(bool constructing, int mode)
                {
                    LocalReturnReadProbe probe = new();
                    Operand receiver = Operand.Location(probe);
                    StageBValue result = StageBValue.Int64(71);
                    StageBPrefixNode node = new(StageBOperationKind.ObjectCreation, "probe", "", "", "", "", false,
                        (StageBPrefixOperandMode)mode, new(-1, StageBRefKind.None, false, StageBArgumentMode.Value, null, []), null, [], [], true);
                    Operand Select() { {{body}} }
                    try
                    {
                        Operand selected = Select();
                        return [probe.Reads, ReferenceEquals(selected.Target, probe) ? 1 : 0, (int)(selected.Immediate?.Signed ?? -1)];
                    }
                    catch (StageBExecutionException exception) when (exception.Message.Contains("poison-read", StringComparison.Ordinal))
                    {
                        return [probe.Reads, 0, -1];
                    }
                }
                private sealed class LocalReturnReadProbe() : Location(false)
                {
                    internal int Reads;
                    internal override string Identity => "return-read-probe";
                    internal override StageBValue Read() { Reads++; throw new StageBExecutionException("poison-read", "receiver read"); }
                    internal override void Write(StageBValue value) => throw new NotSupportedException();
                    internal override StageBResolvedLocation Resolve() => throw new NotSupportedException();
                }
                """;
            CSharpCompilation candidate = Mutate(StageBControlExtractor.InterpreterPath,
                ("internal static class StageBPrefixInterpreter\n{", "internal static class StageBPrefixInterpreter\n{\n" + probe));
            using MemoryStream assembly = new();
            Microsoft.CodeAnalysis.Emit.EmitResult emitted = candidate.Emit(assembly);
            Assert.That(emitted.Success, Is.True, string.Join('\n', emitted.Diagnostics));
            assembly.Position = 0;
            System.Runtime.Loader.AssemblyLoadContext context = new(null, isCollectible: true);
            try
            {
                System.Reflection.Assembly loaded = context.LoadFromStream(assembly);
                System.Reflection.MethodInfo method = loaded.GetType("Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBPrefixInterpreter")!
                    .GetMethod("ProbeLocalReturn", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
                return (int[])method.Invoke(null, [constructing, mode])!;
            }
            finally { context.Unload(); }
        }
    }

    [Test]
    public void Stage_b_control_transparent_adapter_preserves_identity_and_orders_arity_fuel_and_reads()
    {
        const string probe = """
            private static int[] ProbeTransparent(int kind, int mode, int childCount, long fuel, bool poison)
            {
                Machine machine = (Machine)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Machine));
                machine._fuel = fuel;
                Frame frame = new(null!, Operand.Value(StageBValue.Unit));
                TransparentReadProbe readProbe = new(poison);
                Cell cell = new("probe", StageBValue.Unit) { Alias = readProbe };
                frame.Cells.Add("probe", cell);
                StageBTermBinding binding = new(-1, StageBRefKind.None, false, StageBArgumentMode.Value, null, []);
                StageBPrefixNode child = new(StageBOperationKind.LocalReference, "long", "probe", "", "", "", false,
                    StageBPrefixOperandMode.Location, binding, null, [], [], true);
                StageBPrefixNode wrapper = child with
                {
                    Kind = (StageBOperationKind)kind,
                    Mode = (StageBPrefixOperandMode)mode,
                    Children = Enumerable.Repeat(child, childCount).ToArray(),
                };
                try
                {
                    Operand selected = machine.Evaluate(frame, wrapper);
                    int aliases = selected.Target is CellLocation location && ReferenceEquals(location.Cell.Alias, readProbe) ? 1 : 0;
                    return [0, readProbe.Reads, aliases, (int)(selected.Immediate?.Signed ?? -1), (int)machine._fuel];
                }
                catch (FuelSignal)
                {
                    return [1, readProbe.Reads, 0, -1, (int)machine._fuel];
                }
                catch (StageBExecutionException exception) when (exception.Message.Contains("transparent-arity", StringComparison.Ordinal))
                {
                    return [2, readProbe.Reads, 0, -1, (int)machine._fuel];
                }
                catch (StageBExecutionException exception) when (exception.Message.Contains("poison-read", StringComparison.Ordinal))
                {
                    return [3, readProbe.Reads, 0, -1, (int)machine._fuel];
                }
            }
            private sealed class TransparentReadProbe(bool poison) : Location(false)
            {
                internal int Reads;
                internal override string Identity => "transparent-read-probe";
                internal override StageBValue Read()
                {
                    Reads++;
                    if (poison) throw new StageBExecutionException("poison-read", "transparent operand read");
                    return StageBValue.Int64(71);
                }
                internal override void Write(StageBValue value) => throw new NotSupportedException();
                internal override StageBResolvedLocation Resolve() => throw new NotSupportedException();
            }
            """;
        CSharpCompilation candidate = Mutate(StageBControlExtractor.InterpreterPath,
            ("private long _fuel;", "private long _fuel;\n" + probe));
        using MemoryStream assembly = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = candidate.Emit(assembly);
        Assert.That(emitted.Success, Is.True, string.Join('\n', emitted.Diagnostics));
        assembly.Position = 0;
        System.Runtime.Loader.AssemblyLoadContext context = new(null, isCollectible: true);
        try
        {
            System.Reflection.Assembly loaded = context.LoadFromStream(assembly);
            System.Reflection.MethodInfo method = loaded.GetType("Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBPrefixInterpreter+Machine")!
                .GetMethod("ProbeTransparent", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            int[] Run(StageBOperationKind kind, StageBPrefixOperandMode mode, int children = 1, long fuel = 3, bool poison = false) =>
                (int[])method.Invoke(null, [(int)kind, (int)mode, children, fuel, poison])!;

            foreach (StageBOperationKind kind in new[] { StageBOperationKind.Argument, StageBOperationKind.DeclarationExpression })
            foreach (StageBPrefixOperandMode mode in Enum.GetValues<StageBPrefixOperandMode>())
                Assert.That(Run(kind, mode), Is.EqualTo(new[] { 0, 0, 1, -1, 1 }), $"{kind}/{mode}");
            Assert.That(Run(StageBOperationKind.Argument, StageBPrefixOperandMode.Value, poison: true),
                Is.EqualTo(new[] { 0, 0, 1, -1, 1 }), "Argument must not read a poisoned location.");
            Assert.That(Run(StageBOperationKind.ExpressionStatement, StageBPrefixOperandMode.Value),
                Is.EqualTo(new[] { 0, 1, 0, 71, 1 }), "ExpressionStatement value mode reads exactly once.");
            foreach (StageBPrefixOperandMode mode in new[] { StageBPrefixOperandMode.Location, StageBPrefixOperandMode.ReadOnlyLocation })
                Assert.That(Run(StageBOperationKind.ExpressionStatement, mode), Is.EqualTo(new[] { 0, 0, 1, -1, 1 }), mode.ToString());
            foreach (int children in new[] { 0, 2 })
                Assert.That(Run(StageBOperationKind.ExpressionStatement, StageBPrefixOperandMode.Value, children, poison: true),
                    Is.EqualTo(new[] { 2, 0, 0, -1, 2 }), $"arity {children}");
            Assert.That(Run(StageBOperationKind.ExpressionStatement, StageBPrefixOperandMode.Value, fuel: 0),
                Is.EqualTo(new[] { 1, 0, 0, -1, 0 }), "parent tick");
            Assert.That(Run(StageBOperationKind.ExpressionStatement, StageBPrefixOperandMode.Value, fuel: 1),
                Is.EqualTo(new[] { 1, 0, 0, -1, 0 }), "child tick");
            Assert.That(Run(StageBOperationKind.ExpressionStatement, StageBPrefixOperandMode.Value, fuel: 2),
                Is.EqualTo(new[] { 0, 1, 0, 71, 0 }), "exact fuel");
        }
        finally { context.Unload(); }
    }

    [Test]
    public void Stage_b_control_execute_sets_last_from_ordered_reads_and_returns_branch_identity()
    {
        const string probe = """
            private static int[] ProbeExecuteLast()
            {
                Machine machine = (Machine)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Machine));
                machine._fuel = 6;
                List<int> order = [];
                StageBValue old = StageBValue.Int64(7);
                StageBValue operation = StageBValue.Int64(19);
                StageBValue secondOperation = StageBValue.Int64(37);
                StageBValue branch = StageBValue.Int64(71);
                StageBTermBinding binding = new(-1, StageBRefKind.None, false, StageBArgumentMode.Value, null, []);
                StageBPrefixNode Node(string symbol) => new(StageBOperationKind.LocalReference, "long", symbol, "", "", "", false,
                    StageBPrefixOperandMode.Location, binding, null, [], [], true);
                StageBPrefixBlock first = new(0, [Node("old")], null, "None", StageBPrefixExit.Branch,
                    new StageBEdge(1, "Regular", [], [], []), null);
                StageBPrefixBlock second = new(1, [Node("operation"), Node("second-operation")], Node("branch"), "None", StageBPrefixExit.Return, null, null);
                StageBMember member = new("probe", "", "Probe", "probe", StageBMemberKind.Method, StageBReceiverKind.Static,
                    "long", StageBRefKind.None, []);
                StageBPrefixFunction function = new(member, new(0, 0), [], [], [], [], [first, second], [], [], [], 5, true);
                Frame frame = new(function, Operand.Value(StageBValue.Unit));
                frame.Cells.Add("old", new("old", StageBValue.Unit) { Alias = new ExecuteReadProbe(7, old, order) });
                frame.Cells.Add("operation", new("operation", StageBValue.Unit) { Alias = new ExecuteReadProbe(19, operation, order) });
                frame.Cells.Add("second-operation", new("second-operation", StageBValue.Unit) { Alias = new ExecuteReadProbe(37, secondOperation, order) });
                frame.Cells.Add("branch", new("branch", StageBValue.Unit) { Alias = new ExecuteReadProbe(71, branch, order) });
                StageBValue result = machine.Execute(frame);
                return [(int)result.Signed, order.Count, order[0], order[1], order[2], order[3],
                    ReferenceEquals(result, branch) ? 1 : 0, (int)machine._fuel];
            }
            private sealed class ExecuteReadProbe(int marker, StageBValue value, List<int> order) : Location(false)
            {
                internal override string Identity => "execute-read-" + marker;
                internal override StageBValue Read() { order.Add(marker); return value; }
                internal override void Write(StageBValue replacement) => throw new NotSupportedException();
                internal override StageBResolvedLocation Resolve() => throw new NotSupportedException();
            }
            """;
        CSharpCompilation candidate = Mutate(StageBControlExtractor.InterpreterPath,
            ("private long _fuel;", "private long _fuel;\n" + probe));
        using MemoryStream assembly = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = candidate.Emit(assembly);
        Assert.That(emitted.Success, Is.True, string.Join('\n', emitted.Diagnostics));
        assembly.Position = 0;
        System.Runtime.Loader.AssemblyLoadContext context = new(null, isCollectible: true);
        try
        {
            System.Reflection.Assembly loaded = context.LoadFromStream(assembly);
            System.Reflection.MethodInfo method = loaded.GetType("Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBPrefixInterpreter+Machine")!
                .GetMethod("ProbeExecuteLast", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            Assert.That((int[])method.Invoke(null, null)!, Is.EqualTo(new[] { 71, 4, 7, 19, 37, 71, 1, 0 }));
        }
        finally { context.Unload(); }
    }

    [Test]
    public void Stage_b_control_call_binding_preserves_reference_and_positional_copy_boundaries()
    {
        const string probe = """
            private CallBindingProbe? _callBindingProbe;

            private static object[] ProbeCallBinding(int refKind, int sourceKind, int argumentMode, bool positional, bool write, bool read)
            {
                StageBParameter parameter = new("parameter", "value", 0, "long", (StageBRefKind)refKind, false);
                StageBMember member = new("callee", "", "Callee", "probe", StageBMemberKind.Method, StageBReceiverKind.Static,
                    "void", StageBRefKind.None, [parameter]);
                StageBPrefixFunction function = new(member, new(0, 0), [], [], [], [], [], [], [], [], 1, true);
                StageBPrefixProgram program = new("callee", "", [function], [], [], [], null!, [], 1, "", [], [], [], [], "");
                Machine machine = new(program, null!, new(), 1);
                CallBindingProbe observer = new(sourceKind, write, read);
                machine._callBindingProbe = observer;
                Operand actual = sourceKind == 0 ? Operand.Value(StageBValue.Int64(71)) : Operand.Location(observer.Source);
                StageBPrefixCall call = new(new(StageBPrefixTargetKind.Local, member, "callee", null), -1,
                    positional ? [] : [new(0, 0, (StageBArgumentMode)argumentMode, false, "Explicit")]);
                StageBPrefixNode node = new(StageBOperationKind.Invocation, "void", "", "", "", "", false,
                    StageBPrefixOperandMode.Value, new(-1, StageBRefKind.None, false, StageBArgumentMode.Value, null, []),
                    call, [], [], true);
                string outcome = "ok";
                try { _ = machine.Call(new(function, Operand.Value(StageBValue.Unit)), node, call, [actual], false); }
                catch (StageBExecutionException exception) { outcome = exception.Code; }
                return [outcome, observer.Entered, observer.Source.Reads, observer.Source.Writes, observer.Aliased,
                    observer.BoundValue, observer.Source.Value.Signed, observer.Provenance, observer.ReadOnly];
            }

            private sealed class CallBindingProbe(int sourceKind, bool write, bool read)
            {
                internal BindingSource Source { get; } = new(sourceKind == 2, sourceKind == 3);
                internal int Entered;
                internal bool Aliased;
                internal long BoundValue;
                internal string Provenance = "";
                internal bool ReadOnly;

                internal StageBValue Observe(Frame frame)
                {
                    Entered++;
                    Cell bound = frame.Cells["parameter"];
                    Aliased = ReferenceEquals(bound.Alias, Source);
                    BoundValue = bound.Value.Signed;
                    if (bound.Alias is { } alias)
                    {
                        StageBResolvedLocation resolved = alias.Resolve();
                        Provenance = resolved.Provenance;
                        ReadOnly = resolved.ReadOnly;
                    }
                    if (read) BoundValue = new CellLocation(bound, false).Read().Signed;
                    if (write)
                    {
                        new CellLocation(bound, false).Write(StageBValue.Int64(37));
                        BoundValue = bound.Value.Signed;
                    }
                    return StageBValue.Unit;
                }
            }

            private sealed class BindingSource(bool readOnly, bool poison) : Location(readOnly)
            {
                internal int Reads;
                internal int Writes;
                internal StageBValue Value = StageBValue.Int64(71);
                internal override string Identity => "source";
                internal override StageBValue Read()
                {
                    Reads++;
                    if (poison) throw new StageBExecutionException("poison-read", "source");
                    return Value;
                }
                internal override void Write(StageBValue value)
                {
                    if (ReadOnly) throw new StageBExecutionException("readonly", Identity);
                    Writes++;
                    Value = value;
                }
                internal override StageBResolvedLocation Resolve() => new(this, [], ReadOnly, Identity);
            }
            """;
        CSharpCompilation candidate = Mutate(StageBControlExtractor.InterpreterPath,
            ("private long _fuel;", "private long _fuel;\n" + probe),
            ("private StageBValue Execute(Frame frame)\n        {",
                "private StageBValue Execute(Frame frame)\n        {\n            if (_callBindingProbe is { } observer) return observer.Observe(frame);"));
        using MemoryStream assembly = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = candidate.Emit(assembly);
        Assert.That(emitted.Success, Is.True, string.Join('\n', emitted.Diagnostics));
        assembly.Position = 0;
        System.Runtime.Loader.AssemblyLoadContext context = new(null, isCollectible: true);
        try
        {
            System.Reflection.Assembly loaded = context.LoadFromStream(assembly);
            System.Reflection.MethodInfo method = loaded.GetType("Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBPrefixInterpreter+Machine")!
                .GetMethod("ProbeCallBinding", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            object[] Run(StageBRefKind kind, int source, StageBArgumentMode mode, bool positional, bool write = false, bool read = false) =>
                (object[])method.Invoke(null, [(int)kind, source, (int)mode, positional, write, read])!;

            foreach (StageBRefKind kind in Enum.GetValues<StageBRefKind>())
            foreach (int source in new[] { 0, 1, 2, 3 })
            foreach (bool positional in new[] { false, true })
            {
                bool copy = positional || kind == StageBRefKind.None;
                bool rejected = !copy && source == 0 && kind is StageBRefKind.Ref or StageBRefKind.Out;
                bool poisoned = copy && source == 3;
                bool entered = !rejected && !poisoned;
                bool alias = entered && !copy && source != 0;
                object[] expected =
                [
                    rejected ? "argument-location" : poisoned ? "poison-read" : "ok", entered ? 1 : 0,
                    copy && source != 0 ? 1 : 0, 0, alias, entered && copy ? 71L : 0L, 71L,
                    entered && !copy ? source == 0 ? "readonly-expression:parameter" : "source" : "",
                    entered && !copy && source is 0 or 2,
                ];
                Assert.That(Run(kind, source, StageBArgumentMode.Value, positional), Is.EqualTo(expected),
                    $"{kind}/source={source}/positional={positional}");
            }

            foreach (StageBRefKind kind in Enum.GetValues<StageBRefKind>())
            foreach (int source in new[] { 1, 2 })
                Assert.That(Run(kind, source, StageBArgumentMode.Value, positional: true, write: true),
                    Is.EqualTo(new object[] { "ok", 1, 1, 0, false, 37L, 71L, "", false }),
                    $"Positional {kind} copies source {source}, even a readonly location.");
            Assert.That(Run(StageBRefKind.None, 1, StageBArgumentMode.Value, positional: false, write: true),
                Is.EqualTo(new object[] { "ok", 1, 1, 0, false, 37L, 71L, "", false }), "Explicit by-value copy");

            foreach (StageBRefKind kind in Enum.GetValues<StageBRefKind>())
            {
                if (kind == StageBRefKind.None) continue;
                Assert.That(Run(kind, 1, StageBArgumentMode.Value, positional: false, write: true),
                    Is.EqualTo(new object[] { "ok", 1, 0, 1, true, 0L, 37L, "source", false }), $"{kind} aliases the mutable location.");
                Assert.That(Run(kind, 2, StageBArgumentMode.Value, positional: false, write: true),
                    Is.EqualTo(new object[] { "readonly", 1, 0, 0, true, 0L, 71L, "source", true }), $"{kind} retains readonly location protection.");
            }

            foreach (StageBRefKind kind in new[] { StageBRefKind.In, StageBRefKind.RefReadOnly, StageBRefKind.RefReadOnlyParameter })
            foreach (StageBArgumentMode mode in new[] { StageBArgumentMode.Value, StageBArgumentMode.ReadOnlyTemporary })
            {
                string provenance = mode == StageBArgumentMode.ReadOnlyTemporary ? "temporary:parameter" : "readonly-expression:parameter";
                Assert.That(Run(kind, 0, mode, positional: false, read: true),
                    Is.EqualTo(new object[] { "ok", 1, 0, 0, false, 71L, 71L, provenance, true }),
                    $"{kind}/{mode} retains the immediate value in a readonly temporary.");
                Assert.That(Run(kind, 0, mode, positional: false, write: true),
                    Is.EqualTo(new object[] { "readonly", 1, 0, 0, false, 0L, 71L, provenance, true }),
                    $"{kind}/{mode} creates a readonly immediate temporary.");
            }
        }
        finally { context.Unload(); }
    }

    [Test]
    public void Stage_b_frozen_transaction_result_children_evaluate_into_distinct_exact_entry_cells()
    {
        const string probe = """
            private ConstructorEntryProbe? _constructorEntryProbe;

            private static object[] ProbeTransactionResultEntry(string root)
            {
                StageBPrefixProgram program = StageBPrefixCompiler.Compile(StageBLowering.Build(root));
                StageBPrefixFunction callerFunction = program.Functions.Single(static function =>
                    function.Signature.Name == "GasLimitBelowIntrinsicGas");
                StageBPrefixNode node = callerFunction.Blocks.Single().BranchValue!;
                StageBPrefixCall call = node.Call!;
                StageBPrefixFunction calleeFunction = program.Functions.Single(function =>
                    function.Signature.Symbol == call.Target.Body);
                Machine machine = new(program, null!, new(), 18);
                ConstructorEntryProbe observer = new(calleeFunction);
                machine._constructorEntryProbe = observer;
                Frame caller = new(callerFunction, Operand.Value(StageBValue.Unit));
                Cell marker = new("caller-marker", StageBValue.Int64(41));
                caller.Cells.Add("caller-marker", marker);
                StageBValue capture = StageBValue.Int64(43);
                caller.Captures.Add(17, Operand.Value(capture));
                long initialFuel = machine._fuel;
                _ = machine.Execute(caller);
                _ = machine.Execute(caller);
                object[] completed = observer.Result(caller, marker, capture, call, initialFuel - machine._fuel);

                object[] ProbeShort(long fuel)
                {
                    Machine shortMachine = new(program, null!, new(), fuel);
                    ConstructorEntryProbe shortObserver = new(calleeFunction);
                    shortMachine._constructorEntryProbe = shortObserver;
                    Frame shortCaller = new(callerFunction, Operand.Value(StageBValue.Unit));
                    Cell shortMarker = new("short-marker", StageBValue.Int64(47));
                    StageBValue shortCapture = StageBValue.Int64(53);
                    shortCaller.Cells.Add("short-marker", shortMarker);
                    shortCaller.Captures.Add(19, Operand.Value(shortCapture));
                    bool exhausted = false;
                    try { _ = shortMachine.Execute(shortCaller); }
                    catch (FuelSignal) { exhausted = true; }
                    return
                    [
                        exhausted,
                        shortMachine._fuel,
                        shortObserver.Count,
                        ReferenceEquals(shortCaller.Cells["short-marker"], shortMarker),
                        shortCaller.Cells.Count,
                        ReferenceEquals(shortCaller.Captures[19].Immediate, shortCapture),
                        shortCaller.Captures.Count,
                    ];
                }

                return [.. completed, .. ProbeShort(7), .. ProbeShort(8)];
            }

            private sealed class ConstructorEntryProbe(StageBPrefixFunction function)
            {
                private readonly Frame[] _frames = new Frame[2];
                private readonly Cell[] _receivers = new Cell[2];
                private readonly Cell[,] _parameters = new Cell[2, 3];
                private readonly long[] _remainingFuel = new long[2];
                private int _count;

                internal string Target { get; } = function.Signature.Symbol;
                internal int Count => _count;

                internal StageBValue Observe(Frame frame, long remainingFuel)
                {
                    int index = _count++;
                    if (index >= _frames.Length) throw new StageBExecutionException("entry-probe", "too many calls");
                    if (frame.This.Target is not CellLocation receiver) throw new StageBExecutionException("entry-probe", "receiver");
                    _frames[index] = frame;
                    _receivers[index] = receiver.Cell;
                    _remainingFuel[index] = remainingFuel;
                    for (int ordinal = 0; ordinal < function.Signature.Parameters.Length; ordinal++)
                        _parameters[index, ordinal] = frame.Cells[function.Signature.Parameters[ordinal].Symbol];
                    return StageBValue.Unit;
                }

                internal object[] Result(Frame caller, Cell marker, StageBValue capture, StageBPrefixCall call, long consumedFuel)
                {
                    Cell error = _parameters[0, 0];
                    Cell exception = _parameters[0, 1];
                    Cell description = _parameters[0, 2];
                    bool firstDistinct = !ReferenceEquals(_receivers[0], error) &&
                        !ReferenceEquals(_receivers[0], exception) && !ReferenceEquals(_receivers[0], description) &&
                        !ReferenceEquals(error, exception) && !ReferenceEquals(error, description) &&
                        !ReferenceEquals(exception, description);
                    Cell[] observed =
                    [
                        _receivers[0], _parameters[0, 0], _parameters[0, 1], _parameters[0, 2],
                        _receivers[1], _parameters[1, 0], _parameters[1, 1], _parameters[1, 2],
                    ];
                    bool repeatedDistinct = !ReferenceEquals(_frames[0], _frames[1]);
                    for (int index = 0; index < observed.Length; index++)
                    {
                        repeatedDistinct &= !ReferenceEquals(observed[index], marker);
                        for (int earlier = 0; earlier < index; earlier++)
                            repeatedDistinct &= !ReferenceEquals(observed[index], observed[earlier]);
                    }
                    StageBValue receiverValue = _receivers[0].Value;
                    bool aliasesNull = _receivers[0].Alias is null && error.Alias is null &&
                        exception.Alias is null && description.Alias is null &&
                        _receivers[1].Alias is null && _parameters[1, 0].Alias is null &&
                        _parameters[1, 1].Alias is null && _parameters[1, 2].Alias is null;
                    bool exactFrames = !ReferenceEquals(_frames[0], caller) && !ReferenceEquals(_frames[1], caller) &&
                        ReferenceEquals(_frames[0].Function, function) && ReferenceEquals(_frames[1].Function, function);
                    bool secondPayloadExact = _parameters[1, 0].Alias is null &&
                        _parameters[1, 0].Value.Kind == StageBValueKind.Enum &&
                        _parameters[1, 0].Value.Type == "global::Nethermind.Evm.TransactionProcessing.TransactionResult.ErrorType" &&
                        _parameters[1, 0].Value.Signed == 2 &&
                        _parameters[1, 1].Alias is null &&
                        _parameters[1, 1].Value.Kind == StageBValueKind.Enum &&
                        _parameters[1, 1].Value.Type == "global::Nethermind.Evm.EvmExceptionType" &&
                        _parameters[1, 1].Value.Signed == 0 &&
                        _parameters[1, 2].Alias is null &&
                        _parameters[1, 2].Value.Kind == StageBValueKind.Reference &&
                        _parameters[1, 2].Value.Type == "string" &&
                        _parameters[1, 2].Value.Text == "intrinsic gas too low" &&
                        _receivers[1].Value.Type == "global::Nethermind.Evm.TransactionProcessing.TransactionResult" &&
                        _receivers[1].Value.Fields is { Count: 3 } fields &&
                        fields["Error"].Kind == StageBValueKind.Enum && fields["Error"].Signed == 0 &&
                        fields["EvmExceptionType"].Kind == StageBValueKind.Enum && fields["EvmExceptionType"].Signed == 0 &&
                        fields["ErrorDescription"].Kind == StageBValueKind.Null && fields["ErrorDescription"].Type == "string";
                    return
                    [
                        _count,
                        ReferenceEquals(caller.Cells["caller-marker"], marker),
                        caller.Cells.Count,
                        ReferenceEquals(caller.Captures[17].Immediate, capture),
                        caller.Captures.Count,
                        firstDistinct,
                        repeatedDistinct,
                        aliasesNull,
                        string.Join(",", call.Arguments.Select(static argument => $"{argument.Child}:{argument.Ordinal}")),
                        error.Identity,
                        description.Identity,
                        exception.Identity,
                        (int)error.Value.Kind,
                        error.Value.Type,
                        error.Value.Signed,
                        (int)description.Value.Kind,
                        description.Value.Type,
                        description.Value.Text!,
                        (int)exception.Value.Kind,
                        exception.Value.Type,
                        exception.Value.Signed,
                        receiverValue.Type,
                        receiverValue.Fields!.Count,
                        receiverValue.Fields.ContainsKey("Error"),
                        receiverValue.Fields.ContainsKey("EvmExceptionType"),
                        receiverValue.Fields.ContainsKey("ErrorDescription"),
                        consumedFuel,
                        (int)receiverValue.Field("Error").Kind,
                        receiverValue.Field("Error").Type,
                        receiverValue.Field("Error").Signed,
                        (int)receiverValue.Field("EvmExceptionType").Kind,
                        receiverValue.Field("EvmExceptionType").Type,
                        receiverValue.Field("EvmExceptionType").Signed,
                        (int)receiverValue.Field("ErrorDescription").Kind,
                        receiverValue.Field("ErrorDescription").Type,
                        _remainingFuel[0],
                        _remainingFuel[1],
                        exactFrames,
                        secondPayloadExact,
                    ];
                }
            }
            """;
        CSharpCompilation candidate = Mutate(StageBControlExtractor.InterpreterPath,
            ("private long _fuel;", "private long _fuel;\n" + probe),
            ("private StageBValue Execute(Frame frame)\n        {",
                "private StageBValue Execute(Frame frame)\n        {\n            if (_constructorEntryProbe is { } observer && observer.Target == frame.Function.Signature.Symbol) return observer.Observe(frame, _fuel);"));
        using MemoryStream assembly = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = candidate.Emit(assembly);
        Assert.That(emitted.Success, Is.True, string.Join('\n', emitted.Diagnostics));
        assembly.Position = 0;
        System.Runtime.Loader.AssemblyLoadContext context = new(null, isCollectible: true);
        try
        {
            System.Reflection.Assembly loaded = context.LoadFromStream(assembly);
            System.Reflection.MethodInfo method = loaded.GetType("Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBPrefixInterpreter+Machine")!
                .GetMethod("ProbeTransactionResultEntry", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            object[] actual = (object[])method.Invoke(null, [_root])!;
            string constructor = "global::Nethermind.Evm.TransactionProcessing.TransactionResult.TransactionResult(";
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual[0], Is.EqualTo(2));
                Assert.That(actual[1], Is.True);
                Assert.That(actual[2], Is.EqualTo(1));
                Assert.That(actual[3], Is.True);
                Assert.That(actual[4], Is.EqualTo(1));
                Assert.That(actual[5], Is.True);
                Assert.That(actual[6], Is.True);
                Assert.That(actual[7], Is.True);
                Assert.That(actual[8], Is.EqualTo("0:0,1:2,2:1"));
                Assert.That((string)actual[9], Does.StartWith(constructor).And.Contain("::parameter:0:error:"));
                Assert.That((string)actual[10], Does.StartWith(constructor).And.Contain("::parameter:2:errorDescription:"));
                Assert.That((string)actual[11], Does.StartWith(constructor).And.Contain("::parameter:1:evmException:"));
                Assert.That(actual[12], Is.EqualTo((int)StageBValueKind.Enum));
                Assert.That(actual[13], Is.EqualTo("global::Nethermind.Evm.TransactionProcessing.TransactionResult.ErrorType"));
                Assert.That(actual[14], Is.EqualTo(2L));
                Assert.That(actual[15], Is.EqualTo((int)StageBValueKind.Reference));
                Assert.That(actual[16], Is.EqualTo("string"));
                Assert.That(actual[17], Is.EqualTo("intrinsic gas too low"));
                Assert.That(actual[18], Is.EqualTo((int)StageBValueKind.Enum));
                Assert.That(actual[19], Is.EqualTo("global::Nethermind.Evm.EvmExceptionType"));
                Assert.That(actual[20], Is.EqualTo(0L));
                Assert.That(actual[21], Is.EqualTo("global::Nethermind.Evm.TransactionProcessing.TransactionResult"));
                Assert.That(actual[22], Is.EqualTo(3));
                Assert.That(actual[23], Is.True);
                Assert.That(actual[24], Is.True);
                Assert.That(actual[25], Is.True);
                Assert.That(actual[26], Is.EqualTo(18L));
                Assert.That(actual[27], Is.EqualTo((int)StageBValueKind.Enum));
                Assert.That(actual[28], Is.EqualTo("global::Nethermind.Evm.TransactionProcessing.TransactionResult.ErrorType"));
                Assert.That(actual[29], Is.EqualTo(0L));
                Assert.That(actual[30], Is.EqualTo((int)StageBValueKind.Enum));
                Assert.That(actual[31], Is.EqualTo("global::Nethermind.Evm.EvmExceptionType"));
                Assert.That(actual[32], Is.EqualTo(0L));
                Assert.That(actual[33], Is.EqualTo((int)StageBValueKind.Null));
                Assert.That(actual[34], Is.EqualTo("string"));
                Assert.That(actual[35], Is.EqualTo(9L));
                Assert.That(actual[36], Is.Zero);
                Assert.That(actual[37], Is.True);
                Assert.That(actual[38], Is.True);
                Assert.That(actual[39], Is.True);
                Assert.That(actual[40], Is.Zero);
                Assert.That(actual[41], Is.Zero);
                Assert.That(actual[42], Is.True);
                Assert.That(actual[43], Is.EqualTo(1));
                Assert.That(actual[44], Is.True);
                Assert.That(actual[45], Is.EqualTo(1));
                Assert.That(actual[46], Is.True);
                Assert.That(actual[47], Is.Zero);
                Assert.That(actual[48], Is.Zero);
                Assert.That(actual[49], Is.True);
                Assert.That(actual[50], Is.EqualTo(1));
                Assert.That(actual[51], Is.True);
                Assert.That(actual[52], Is.EqualTo(1));
            }
        }
        finally { context.Unload(); }
    }

    [Test]
    public void Stage_b_frozen_transaction_result_static_field_publishes_only_after_normal_initializer_return()
    {
        const string probe = """
            private StaticFieldEntryProbe? _staticFieldEntryProbe;

            private static object[] ProbeTransactionResultStaticField(string root)
            {
                StageBPrefixProgram program = StageBPrefixCompiler.Compile(StageBLowering.Build(root));
                StageBPrefixFunction callerFunction = program.Functions.Single(static function =>
                    function.Signature.Name == "CalculateAvailableGas");
                StageBPrefixNode field = callerFunction.Blocks.Single(static block => block.Ordinal == 3)
                    .Operations.Single().Children.Single();
                StageBPrefixCall fieldCall = field.Call!;
                StageBPrefixFunction initializer = program.Functions.Single(function =>
                    function.Signature.Symbol == fieldCall.Target.Body);
                StageBPrefixCall constructorCall = initializer.Blocks.Single().BranchValue!.Call!;
                StageBPrefixFunction constructor = program.Functions.Single(function =>
                    function.Signature.Symbol == constructorCall.Target.Body);

                Frame Caller(out Cell marker, out StageBValue capture, out StageBValue receiver)
                {
                    receiver = StageBValue.Reference("probe-caller", "receiver");
                    Frame caller = new(callerFunction, Operand.Value(receiver));
                    marker = new("static-caller-marker", StageBValue.Int64(59));
                    capture = StageBValue.Int64(61);
                    caller.Cells.Add("static-caller-marker", marker);
                    caller.Captures.Add(23, Operand.Value(capture));
                    return caller;
                }

                bool CallerPreserved(Frame caller, Cell marker, StageBValue capture, StageBValue receiver) =>
                    ReferenceEquals(caller.This.Immediate, receiver) && caller.Cells.Count == 1 &&
                    ReferenceEquals(caller.Cells["static-caller-marker"], marker) && marker.Alias is null &&
                    marker.Value.Kind == StageBValueKind.Int64 && marker.Value.Signed == 59 && caller.Captures.Count == 1 &&
                    ReferenceEquals(caller.Captures[23].Immediate, capture);

                Machine entryMachine = new(program, null!, new(), 10);
                StaticFieldEntryProbe entryObserver = new(initializer, constructor, stopAtConstructor: true);
                entryMachine._staticFieldEntryProbe = entryObserver;
                Frame entryCaller = Caller(out Cell entryMarker, out StageBValue entryCapture, out StageBValue entryReceiver);
                bool stoppedAtEntry = false;
                try { _ = entryMachine.Evaluate(entryCaller, field); }
                catch (StaticFieldEntrySignal) { stoppedAtEntry = true; }

                Machine completedMachine = new(program, null!, new(), 29);
                StaticFieldEntryProbe completedObserver = new(initializer, constructor, stopAtConstructor: false);
                completedMachine._staticFieldEntryProbe = completedObserver;
                Frame completedCaller = Caller(out Cell completedMarker, out StageBValue completedCapture, out StageBValue completedReceiver);
                Operand first = completedMachine.Evaluate(completedCaller, field);
                long coldRemaining = completedMachine._fuel;
                Cell cached = completedMachine._statics[fieldCall.Target.Member.Symbol];
                Operand second = completedMachine.Evaluate(completedCaller, field);
                StageBValue completedValue = first.Immediate!;
                bool completedExact = ExactTransactionResult(completedValue) && first.Target is null && second.Target is null &&
                    ReferenceEquals(first.Immediate, second.Immediate) && ReferenceEquals(first.Immediate, cached.Value) &&
                    ReferenceEquals(first.Immediate, completedObserver.ConstructorReceiver.Value);
                bool cachedExact = completedMachine._statics.Count == 1 &&
                    completedMachine._statics.ContainsKey(fieldCall.Target.Member.Symbol) &&
                    cached.Identity == "static:" + fieldCall.Target.Member.Symbol && cached.Alias is null &&
                    !ReferenceEquals(cached, completedMarker) &&
                    completedObserver.ConstructorCells.All(cell => !ReferenceEquals(cell, cached));

                Machine exactMachine = new(program, null!, new(), 28);
                StaticFieldEntryProbe exactObserver = new(initializer, constructor, stopAtConstructor: false);
                exactMachine._staticFieldEntryProbe = exactObserver;
                Frame exactCaller = Caller(out Cell exactMarker, out StageBValue exactCapture, out StageBValue exactReceiver);
                Operand exact = exactMachine.Evaluate(exactCaller, field);
                bool exactBoundary = exactMachine._fuel == 0 && exactMachine._statics.Count == 1 &&
                    exact.Target is null && ExactTransactionResult(exact.Immediate!) && exactObserver.ExactReturns() &&
                    CallerPreserved(exactCaller, exactMarker, exactCapture, exactReceiver) && exactMachine._requests.Count == 0;

                Machine failedMachine = new(program, null!, new(), 27);
                StaticFieldEntryProbe failedObserver = new(initializer, constructor, stopAtConstructor: false);
                failedMachine._staticFieldEntryProbe = failedObserver;
                Frame failedCaller = Caller(out Cell failedMarker, out StageBValue failedCapture, out StageBValue failedReceiver);
                bool exhausted = false;
                try { _ = failedMachine.Evaluate(failedCaller, field); }
                catch (FuelSignal) { exhausted = true; }

                return
                [
                    stoppedAtEntry,
                    entryObserver.Count,
                    entryObserver.Sequence,
                    entryObserver.Fuels,
                    entryObserver.StaticCounts,
                    entryObserver.ExactEntry(entryCaller, entryMarker),
                    CallerPreserved(entryCaller, entryMarker, entryCapture, entryReceiver),
                    entryMachine._statics.Count,
                    entryMachine._requests.Count,
                    string.Join(",", constructorCall.Arguments.Select(static argument => $"{argument.Child}:{argument.Ordinal}")),
                    completedObserver.Count,
                    completedObserver.Sequence,
                    completedObserver.Fuels,
                    coldRemaining,
                    completedMachine._fuel,
                    completedExact,
                    cachedExact,
                    CallerPreserved(completedCaller, completedMarker, completedCapture, completedReceiver),
                    completedMachine._requests.Count,
                    exhausted,
                    failedMachine._fuel,
                    failedObserver.Count,
                    failedObserver.Sequence,
                    failedObserver.Fuels,
                    ExactTransactionResult(failedObserver.ConstructorReceiver.Value),
                    failedMachine._statics.Count,
                    CallerPreserved(failedCaller, failedMarker, failedCapture, failedReceiver),
                    failedMachine._requests.Count,
                    entryObserver.ReturnCount,
                    completedObserver.ReturnCount,
                    completedObserver.ReturnSequence,
                    completedObserver.ReturnFuels,
                    completedObserver.ReturnStaticCounts,
                    completedObserver.ExactReturns(),
                    failedObserver.ReturnCount,
                    exactBoundary,
                    exactObserver.Fuels,
                    exactObserver.ReturnFuels,
                    exactObserver.ReturnStaticCounts,
                    failedObserver.ParametersExact(),
                ];
            }

            private static bool ExactTransactionResult(StageBValue value) =>
                value.Kind == StageBValueKind.Struct &&
                value.Type == "global::Nethermind.Evm.TransactionProcessing.TransactionResult" &&
                value.Fields is { Count: 3 } fields &&
                fields["Error"].Kind == StageBValueKind.Enum &&
                fields["Error"].Type == "global::Nethermind.Evm.TransactionProcessing.TransactionResult.ErrorType" &&
                fields["Error"].Signed == 2 &&
                fields["EvmExceptionType"].Kind == StageBValueKind.Enum &&
                fields["EvmExceptionType"].Type == "global::Nethermind.Evm.EvmExceptionType" &&
                fields["EvmExceptionType"].Signed == 0 &&
                fields["ErrorDescription"].Kind == StageBValueKind.Reference &&
                fields["ErrorDescription"].Type == "string" &&
                fields["ErrorDescription"].Text == "intrinsic gas too low";

            private sealed class StaticFieldEntrySignal : Exception;

            private sealed class StaticFieldEntryProbe(
                StageBPrefixFunction initializer, StageBPrefixFunction constructor, bool stopAtConstructor)
            {
                private readonly Frame[] _frames = new Frame[2];
                private readonly long[] _fuels = new long[2];
                private readonly int[] _staticCounts = new int[2];
                private readonly Cell[] _constructorCells = new Cell[4];
                private readonly Frame[] _returnFrames = new Frame[2];
                private readonly StageBValue[] _returnValues = new StageBValue[2];
                private readonly long[] _returnFuels = new long[2];
                private readonly int[] _returnStaticCounts = new int[2];
                private int _count;
                private int _returnCount;

                internal int Count => _count;
                internal string Sequence => string.Join(",", _frames.Take(_count).Select(static frame => frame.Function.Signature.Name));
                internal string Fuels => string.Join(",", _fuels.Take(_count));
                internal string StaticCounts => string.Join(",", _staticCounts.Take(_count));
                internal int ReturnCount => _returnCount;
                internal string ReturnSequence => string.Join(",", _returnFrames.Take(_returnCount).Select(static frame => frame.Function.Signature.Name));
                internal string ReturnFuels => string.Join(",", _returnFuels.Take(_returnCount));
                internal string ReturnStaticCounts => string.Join(",", _returnStaticCounts.Take(_returnCount));
                internal Cell ConstructorReceiver => _constructorCells[0];
                internal Cell[] ConstructorCells => _constructorCells;

                internal void Observe(Frame frame, long fuel, int staticCount)
                {
                    if (_count >= _frames.Length) throw new StageBExecutionException("static-entry-probe", "too many calls");
                    _frames[_count] = frame;
                    _fuels[_count] = fuel;
                    _staticCounts[_count] = staticCount;
                    _count++;
                    if (ReferenceEquals(frame.Function, constructor))
                    {
                        _constructorCells[0] = ((CellLocation)frame.This.Target!).Cell;
                        for (int index = 0; index < constructor.Signature.Parameters.Length; index++)
                            _constructorCells[index + 1] = frame.Cells[constructor.Signature.Parameters[index].Symbol];
                    }
                    if (stopAtConstructor && ReferenceEquals(frame.Function, constructor)) throw new StaticFieldEntrySignal();
                }

                internal void ObserveReturn(Frame frame, StageBValue value, long fuel, int staticCount)
                {
                    if (_returnCount >= _returnFrames.Length) throw new StageBExecutionException("static-return-probe", "too many returns");
                    _returnFrames[_returnCount] = frame;
                    _returnValues[_returnCount] = value;
                    _returnFuels[_returnCount] = fuel;
                    _returnStaticCounts[_returnCount] = staticCount;
                    _returnCount++;
                }

                internal bool ExactReturns()
                {
                    if (_returnCount != 2 || !ReferenceEquals(_returnFrames[0], _frames[1]) ||
                        !ReferenceEquals(_returnFrames[1], _frames[0])) return false;
                    Cell description = _frames[1].Cells[constructor.Signature.Parameters[2].Symbol];
                    return ParametersExact() && ReferenceEquals(_returnValues[0], description.Value) &&
                        _returnValues[0].Kind == StageBValueKind.Reference && _returnValues[0].Type == "string" &&
                        _returnValues[0].Text == "intrinsic gas too low" &&
                        ReferenceEquals(_returnValues[1], ConstructorReceiver.Value) &&
                        !ReferenceEquals(_returnValues[0], _returnValues[1]) && ExactTransactionResult(_returnValues[1]);
                }

                internal bool ParametersExact()
                {
                    Cell error = _frames[1].Cells[constructor.Signature.Parameters[0].Symbol];
                    Cell exception = _frames[1].Cells[constructor.Signature.Parameters[1].Symbol];
                    Cell description = _frames[1].Cells[constructor.Signature.Parameters[2].Symbol];
                    return ReferenceEquals(((CellLocation)_frames[1].This.Target!).Cell, _constructorCells[0]) &&
                        ReferenceEquals(error, _constructorCells[1]) && ReferenceEquals(exception, _constructorCells[2]) &&
                        ReferenceEquals(description, _constructorCells[3]) &&
                        error.Alias is null && exception.Alias is null && description.Alias is null &&
                        error.Value.Kind == StageBValueKind.Enum &&
                        error.Value.Type == "global::Nethermind.Evm.TransactionProcessing.TransactionResult.ErrorType" &&
                        error.Value.Signed == 2 && exception.Value.Kind == StageBValueKind.Enum &&
                        exception.Value.Type == "global::Nethermind.Evm.EvmExceptionType" && exception.Value.Signed == 0 &&
                        description.Value.Kind == StageBValueKind.Reference && description.Value.Type == "string" &&
                        description.Value.Text == "intrinsic gas too low";
                }

                internal bool ExactEntry(Frame caller, Cell marker)
                {
                    if (_count != 2 || !ReferenceEquals(_frames[0].Function, initializer) ||
                        !ReferenceEquals(_frames[1].Function, constructor) || ReferenceEquals(_frames[0], caller) ||
                        ReferenceEquals(_frames[1], caller) || ReferenceEquals(_frames[0], _frames[1]) ||
                        !ReferenceEquals(_frames[0].This.Immediate, StageBValue.Unit) ||
                        _frames[0].Cells.Count != 0 || _frames[0].Captures.Count != 0 ||
                        _frames[1].Captures.Count != 0 || _frames[1].This.Target is not CellLocation receiver ||
                        receiver.Resolve().ReadOnly || receiver.Cell.Alias is not null ||
                        _frames[1].Cells.Count != 3) return false;

                    Cell error = _frames[1].Cells[constructor.Signature.Parameters[0].Symbol];
                    Cell exception = _frames[1].Cells[constructor.Signature.Parameters[1].Symbol];
                    Cell description = _frames[1].Cells[constructor.Signature.Parameters[2].Symbol];
                    Cell[] cells = [receiver.Cell, error, exception, description];
                    for (int index = 0; index < cells.Length; index++)
                    {
                        if (cells[index].Alias is not null || ReferenceEquals(cells[index], marker)) return false;
                        for (int earlier = 0; earlier < index; earlier++)
                            if (ReferenceEquals(cells[index], cells[earlier])) return false;
                    }

                    StageBValue receiverValue = receiver.Cell.Value;
                    return error.Value.Kind == StageBValueKind.Enum &&
                        error.Value.Type == "global::Nethermind.Evm.TransactionProcessing.TransactionResult.ErrorType" &&
                        error.Value.Signed == 2 && exception.Value.Kind == StageBValueKind.Enum &&
                        exception.Value.Type == "global::Nethermind.Evm.EvmExceptionType" && exception.Value.Signed == 0 &&
                        description.Value.Kind == StageBValueKind.Reference && description.Value.Type == "string" &&
                        description.Value.Text == "intrinsic gas too low" &&
                        receiverValue.Fields is { Count: 3 } fields &&
                        fields["Error"].Kind == StageBValueKind.Enum && fields["Error"].Signed == 0 &&
                        fields["EvmExceptionType"].Kind == StageBValueKind.Enum && fields["EvmExceptionType"].Signed == 0 &&
                        fields["ErrorDescription"].Kind == StageBValueKind.Null && fields["ErrorDescription"].Type == "string";
                }
            }
            """;
        CSharpCompilation candidate = Mutate(StageBControlExtractor.InterpreterPath,
            ("private long _fuel;", "private long _fuel;\n" + probe),
            ("private StageBValue Execute(Frame frame)\n        {",
                "private StageBValue Execute(Frame frame)\n        {\n            _staticFieldEntryProbe?.Observe(frame, _fuel, _statics.Count);"),
            ("if (result.Kind == StageBEdgeKind.Return) return result.Cursor.Carried;", """
                if (result.Kind == StageBEdgeKind.Return)
                {
                    _staticFieldEntryProbe?.ObserveReturn(frame, result.Cursor.Carried, _fuel, _statics.Count);
                    return result.Cursor.Carried;
                }
                """));
        using MemoryStream assembly = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = candidate.Emit(assembly);
        Assert.That(emitted.Success, Is.True, string.Join('\n', emitted.Diagnostics));
        assembly.Position = 0;
        System.Runtime.Loader.AssemblyLoadContext context = new(null, isCollectible: true);
        try
        {
            System.Reflection.Assembly loaded = context.LoadFromStream(assembly);
            System.Reflection.MethodInfo method = loaded.GetType("Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBPrefixInterpreter+Machine")!
                .GetMethod("ProbeTransactionResultStaticField", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            object[] actual = (object[])method.Invoke(null, [_root])!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual[0], Is.True);
                Assert.That(actual[1], Is.EqualTo(2));
                Assert.That(actual[2], Is.EqualTo("GasLimitBelowIntrinsicGas,.ctor"));
                Assert.That(actual[3], Is.EqualTo("9,0"));
                Assert.That(actual[4], Is.EqualTo("0,0"));
                Assert.That(actual[5], Is.True);
                Assert.That(actual[6], Is.True);
                Assert.That(actual[7], Is.Zero);
                Assert.That(actual[8], Is.Zero);
                Assert.That(actual[9], Is.EqualTo("0:0,1:2,2:1"));
                Assert.That(actual[10], Is.EqualTo(2));
                Assert.That(actual[11], Is.EqualTo("GasLimitBelowIntrinsicGas,.ctor"));
                Assert.That(actual[12], Is.EqualTo("28,19"));
                Assert.That(actual[13], Is.EqualTo(1L));
                Assert.That(actual[14], Is.Zero);
                Assert.That(actual[15], Is.True);
                Assert.That(actual[16], Is.True);
                Assert.That(actual[17], Is.True);
                Assert.That(actual[18], Is.Zero);
                Assert.That(actual[19], Is.True);
                Assert.That(actual[20], Is.Zero);
                Assert.That(actual[21], Is.EqualTo(2));
                Assert.That(actual[22], Is.EqualTo("GasLimitBelowIntrinsicGas,.ctor"));
                Assert.That(actual[23], Is.EqualTo("26,17"));
                Assert.That(actual[24], Is.True);
                Assert.That(actual[25], Is.Zero);
                Assert.That(actual[26], Is.True);
                Assert.That(actual[27], Is.Zero);
                Assert.That(actual[28], Is.Zero);
                Assert.That(actual[29], Is.EqualTo(2));
                Assert.That(actual[30], Is.EqualTo(".ctor,GasLimitBelowIntrinsicGas"));
                Assert.That(actual[31], Is.EqualTo("1,1"));
                Assert.That(actual[32], Is.EqualTo("0,0"));
                Assert.That(actual[33], Is.True);
                Assert.That(actual[34], Is.Zero);
                Assert.That(actual[35], Is.True);
                Assert.That(actual[36], Is.EqualTo("27,18"));
                Assert.That(actual[37], Is.EqualTo("0,0"));
                Assert.That(actual[38], Is.EqualTo("0,0"));
                Assert.That(actual[39], Is.True);
            }
        }
        finally { context.Unload(); }
    }

    [Test]
    public void Stage_b_frozen_ok_static_field_preserves_callers_and_exact_publication_boundaries()
    {
        const string probe = """
            private OkStaticProbe? _okStaticProbe;

            private static Dictionary<string, object>[] ProbeOkStaticField(string root)
            {
                StageBPrefixProgram program = StageBPrefixCompiler.Compile(StageBLowering.Build(root));
                StageBPrefixFunction function = program.Functions.Single(static item => item.Signature.Name == "CalculateAvailableGas");
                StageBPrefixNode node = function.Blocks.Single(static block => block.Ordinal == 2).Operations.Single().Children.Single();
                string symbol = node.Call!.Target.Member.Symbol;
                StageBPrefixFunction initializer = program.Functions.Single(item => item.Signature.Symbol == node.Call.Target.Body);
                List<Dictionary<string, object>> rows = [];
                for (long fuel = 0; fuel <= 4; fuel++)
                {
                    StageBValue receiver = StageBValue.Reference("ok-caller", "receiver");
                    Frame caller = new(function, Operand.Value(receiver));
                    Cell marker = new("ok-caller-marker", StageBValue.Int64(59));
                    caller.Cells.Add(marker.Identity, marker);
                    StageBValue capture = StageBValue.Int64(61);
                    caller.Captures.Add(23, Operand.Value(capture));
                    StageBCodeLookupRequest request = new("untouched", false);
                    StageBCodeLookupExchange exchange = new(request, true, null);
                    StageBResponseTape tape = new(exchange);
                    Machine machine = new(program, null!, tape, fuel);
                    Cell previousStatic = new("unrelated-static", StageBValue.Int64(67));
                    machine._statics.Add("unrelated-static", previousStatic);
                    machine._requests.Add(request);
                    OkStaticProbe observer = new(initializer, caller, symbol);
                    machine._okStaticProbe = observer;
                    Operand? first = null;
                    bool exhausted = false;
                    try { first = machine.Evaluate(caller, node); }
                    catch (FuelSignal) { exhausted = true; }
                    long coldRemaining = machine._fuel;
                    bool valueExact = first is null;
                    bool cachedIdentity = true;
                    bool cachedExhausted = false;
                    if (first is { } firstOperand)
                    {
                        valueExact = firstOperand.Target is null && ExactOk(firstOperand.Immediate!);
                        Cell cached = machine._statics[symbol];
                        cachedIdentity = cached.Identity == "static:" + symbol && cached.Alias is null &&
                            ReferenceEquals(firstOperand.Immediate, cached.Value) &&
                            ReferenceEquals(firstOperand.Immediate, observer.ReturnedValue) &&
                            !ReferenceEquals(cached, marker) && !ReferenceEquals(cached, previousStatic);
                        try
                        {
                            Operand second = machine.Evaluate(caller, node);
                            cachedIdentity &= second.Target is null && ReferenceEquals(firstOperand.Immediate, second.Immediate) &&
                                ReferenceEquals(machine._statics[symbol], cached);
                        }
                        catch (FuelSignal) { cachedExhausted = true; }
                    }
                    rows.Add(new()
                    {
                        ["fuel"] = fuel,
                        ["exhausted"] = exhausted,
                        ["coldRemaining"] = coldRemaining,
                        ["remaining"] = machine._fuel,
                        ["entries"] = observer.Entries,
                        ["returns"] = observer.Returns,
                        ["entryFuel"] = observer.EntryFuel,
                        ["returnFuel"] = observer.ReturnFuel,
                        ["cachedExhausted"] = cachedExhausted,
                        ["staticCount"] = machine._statics.Count,
                        ["preserved"] = ReferenceEquals(caller.This.Immediate, receiver) && caller.Cells.Count == 1 &&
                            ReferenceEquals(caller.Cells[marker.Identity], marker) && marker.Alias is null &&
                            marker.Value.Kind == StageBValueKind.Int64 && marker.Value.Signed == 59 && caller.Captures.Count == 1 &&
                            ReferenceEquals(caller.Captures[23].Immediate, capture) &&
                            ReferenceEquals(machine._statics["unrelated-static"], previousStatic) && previousStatic.Alias is null &&
                            previousStatic.Value.Kind == StageBValueKind.Int64 && previousStatic.Value.Signed == 67 &&
                            machine._requests.Count == 1 && ReferenceEquals(machine._requests[0], request) &&
                            tape.Consumed == 0 && tape.Remaining == 1 && ReferenceEquals(tape.RemainingExchanges[0], exchange),
                        ["valueExact"] = valueExact,
                        ["cachedIdentity"] = cachedIdentity,
                        ["normalPublication"] = observer.Unpublished && observer.FrameExact,
                    });
                }
                return rows.ToArray();
            }

            private static bool ExactOk(StageBValue value) =>
                value.Kind == StageBValueKind.Struct &&
                value.Type == "global::Nethermind.Evm.TransactionProcessing.TransactionResult" &&
                value.Fields is { Count: 3 } fields &&
                fields["Error"].Kind == StageBValueKind.Enum &&
                fields["Error"].Type == "global::Nethermind.Evm.TransactionProcessing.TransactionResult.ErrorType" &&
                fields["Error"].Signed == 0 && fields["EvmExceptionType"].Kind == StageBValueKind.Enum &&
                fields["EvmExceptionType"].Type == "global::Nethermind.Evm.EvmExceptionType" &&
                fields["EvmExceptionType"].Signed == 0 && fields["ErrorDescription"].Kind == StageBValueKind.Null &&
                fields["ErrorDescription"].Type == "string";

            private sealed class OkStaticProbe(StageBPrefixFunction initializer, Frame caller, string symbol)
            {
                internal int Entries;
                internal int Returns;
                internal long EntryFuel = -1;
                internal long ReturnFuel = -1;
                internal bool Unpublished = true;
                internal bool FrameExact = true;
                internal StageBValue? ReturnedValue;
                private Frame? _entered;

                internal void Enter(Frame frame, long fuel, Dictionary<string, Cell> statics)
                {
                    Entries++;
                    EntryFuel = fuel;
                    _entered = frame;
                    Unpublished &= !statics.ContainsKey(symbol);
                    FrameExact &= ReferenceEquals(frame.Function, initializer) && !ReferenceEquals(frame, caller) &&
                        ReferenceEquals(frame.This.Immediate, StageBValue.Unit) && frame.This.Target is null &&
                        frame.Cells.Count == 0 && frame.Captures.Count == 0;
                }

                internal void Return(Frame frame, StageBValue value, long fuel, Dictionary<string, Cell> statics)
                {
                    Returns++;
                    ReturnFuel = fuel;
                    ReturnedValue = value;
                    Unpublished &= !statics.ContainsKey(symbol);
                    FrameExact &= ReferenceEquals(frame, _entered) && frame.Cells.Count == 0 && frame.Captures.Count == 0;
                }
            }
            """;
        CSharpCompilation candidate = Mutate(StageBControlExtractor.InterpreterPath,
            ("private long _fuel;", "private long _fuel;\n" + probe),
            ("private StageBValue Execute(Frame frame)\n        {",
                "private StageBValue Execute(Frame frame)\n        {\n            _okStaticProbe?.Enter(frame, _fuel, _statics);"),
            ("if (result.Kind == StageBEdgeKind.Return) return result.Cursor.Carried;", """
                if (result.Kind == StageBEdgeKind.Return)
                {
                    _okStaticProbe?.Return(frame, result.Cursor.Carried, _fuel, _statics);
                    return result.Cursor.Carried;
                }
                """));
        using MemoryStream assembly = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = candidate.Emit(assembly);
        Assert.That(emitted.Success, Is.True, string.Join('\n', emitted.Diagnostics));
        assembly.Position = 0;
        System.Runtime.Loader.AssemblyLoadContext context = new(null, isCollectible: true);
        try
        {
            System.Reflection.MethodInfo method = context.LoadFromStream(assembly)
                .GetType("Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBPrefixInterpreter+Machine")!
                .GetMethod("ProbeOkStaticField", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            Dictionary<string, object>[] rows = (Dictionary<string, object>[])method.Invoke(null, [_root])!;
            Assert.That(rows, Has.Length.EqualTo(5));
            foreach (Dictionary<string, object> row in rows)
            {
                long fuel = (long)row["fuel"];
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(row["exhausted"], Is.EqualTo(fuel < 3), $"fuel {fuel}");
                    Assert.That(row["coldRemaining"], Is.EqualTo(Math.Max(0, fuel - 3)), $"fuel {fuel}");
                    Assert.That(row["remaining"], Is.EqualTo(0L), $"fuel {fuel}");
                    Assert.That(row["entries"], Is.EqualTo(fuel == 0 ? 0 : 1), $"fuel {fuel}");
                    Assert.That(row["returns"], Is.EqualTo(fuel >= 3 ? 1 : 0), $"fuel {fuel}");
                    Assert.That(row["entryFuel"], Is.EqualTo(fuel == 0 ? -1L : fuel - 1), $"fuel {fuel}");
                    Assert.That(row["returnFuel"], Is.EqualTo(fuel < 3 ? -1L : fuel - 3), $"fuel {fuel}");
                    Assert.That(row["cachedExhausted"], Is.EqualTo(fuel == 3), $"fuel {fuel}");
                    Assert.That(row["staticCount"], Is.EqualTo(fuel >= 3 ? 2 : 1), $"fuel {fuel}");
                    foreach (string observation in new[] { "preserved", "valueExact", "cachedIdentity", "normalPublication" })
                        Assert.That(row[observation], Is.True, $"{observation}, fuel {fuel}");
                }
            }
        }
        finally { context.Unload(); }
    }

    [Test]
    public void Stage_b_calculate_available_gas_composes_copyback_and_ok_publication(
        [Values("baseline", "out-copy", "skip-copyback", "projection", "early-publication", "skip-terminal")] string mutation)
    {
        const string probe = """
            private AvailableGasProbe? _availableGasProbe;

            private static Dictionary<string, object>[] ProbeAvailableGas(string root, bool changeProjection)
            {
                Dictionary<string, string>? overrides = null;
                if (changeProjection)
                {
                    string path = Extractor.EthereumGasPolicyPath;
                    string source = File.ReadAllText(Path.Combine(root, path));
                    overrides = new(StringComparer.Ordinal)
                    {
                        [path] = source.Replace("StateGasSpillRefunded = result.StateGasSpillRefunded,",
                            "StateGasSpillRefunded = result.StateGasSpill,", StringComparison.Ordinal),
                    };
                }
                StageBPrefixProgram program = StageBPrefixCompiler.Compile(StageBLowering.Build(root, overrides));
                StageBPrefixFunction function = program.Functions.Single(static item => item.Signature.Name == "CalculateAvailableGas");
                StageBPrefixNode okNode = function.Blocks.Single(static block => block.Ordinal == 2).Operations.Single().Children.Single();
                string okSymbol = okNode.Call!.Target.Member.Symbol;
                StageBPostNonceInput input = new(
                    new("sender", "recipient", 7, [], 20_000_000), new(true, false), new(),
                    new(new(21_000, 3_000, 17, 19, 23), new(42_000, 29, 31, 37, 41)),
                    false, true, false, false, 2, 1, 100_000, 0, 16_777_216, 183_600);
                input.Validate();
                List<Dictionary<string, object>> rows = [];
                foreach ((long fuel, bool cached) in new (long, bool)[]
                    { (68, false), (69, false), (72, false), (75, false), (76, false), (77, false),
                      (79, false), (80, false), (function.FuelBound, false), (77, true), (78, true) })
                {
                    StageBValue receiver = StageBValue.Reference("available-caller", "receiver");
                    Frame caller = new(function, Operand.Value(receiver));
                    StageBValue intrinsic = input.IntrinsicGas.ToValue();
                    StageBValue sentinel = new StageBGasValue(43, 47, 53, 59, 61).ToValue();
                    Cell output = new("available-output", sentinel);
                    CellLocation outputLocation = new(output, readOnly: false);
                    StageBValue[] values =
                    [
                        StageBValue.Reference(function.Signature.Parameters[0].Type, "tx"),
                        StageBValue.Reference(function.Signature.Parameters[1].Type, "spec"),
                        intrinsic, StageBValue.Null("TGasPolicy"),
                    ];
                    Cell[] parameters = new Cell[4];
                    for (int index = 0; index < parameters.Length; index++)
                    {
                        StageBParameter parameter = function.Signature.Parameters[index];
                        parameters[index] = new(parameter.Symbol, values[index]);
                        caller.Cells.Add(parameter.Symbol, parameters[index]);
                    }
                    parameters[3].Alias = outputLocation;
                    Cell marker = new("available-marker", StageBValue.Int64(67));
                    caller.Cells.Add(marker.Identity, marker);
                    StageBValue capture = StageBValue.Int64(71);
                    caller.Captures.Add(23, Operand.Value(capture));
                    StageBCodeLookupRequest request = new("untouched", false);
                    StageBCodeLookupExchange exchange = new(request, true, null);
                    StageBResponseTape tape = new(exchange);
                    Machine machine = new(program, input, tape, fuel);
                    Cell otherStatic = new("unrelated-static", StageBValue.Int64(73));
                    machine._statics.Add("unrelated-static", otherStatic);
                    machine._requests.Add(request);
                    Cell? existing = null;
                    if (cached)
                    {
                        existing = new("static:" + okSymbol, Default(okNode.Type));
                        machine._statics.Add(okSymbol, existing);
                    }
                    AvailableGasProbe observer = new(caller, parameters, output, intrinsic, okSymbol, fuel, cached);
                    machine._availableGasProbe = observer;
                    StageBValue? returned = null;
                    bool exhausted = false;
                    try { returned = machine.Execute(caller); }
                    catch (FuelSignal) { exhausted = true; }
                    bool published = machine._statics.TryGetValue(okSymbol, out Cell? staticCell);
                    bool preserved = ReferenceEquals(caller.This.Immediate, receiver) && caller.This.Target is null &&
                        caller.Cells.Count == 5 && ReferenceEquals(caller.Cells[marker.Identity], marker) &&
                        marker.Alias is null && marker.Value.Signed == 67 &&
                        ReferenceEquals(caller.Captures[23].Immediate, capture) &&
                        ReferenceEquals(machine._statics["unrelated-static"], otherStatic) &&
                        otherStatic.Alias is null && otherStatic.Value.Signed == 73 &&
                        machine._requests.Count == 1 && ReferenceEquals(machine._requests[0], request) &&
                        tape.Consumed == 0 && tape.Remaining == 1 && ReferenceEquals(tape.RemainingExchanges[0], exchange);
                    for (int index = 0; index < parameters.Length; index++)
                        preserved &= ReferenceEquals(caller.Cells[function.Signature.Parameters[index].Symbol], parameters[index]) &&
                            ReferenceEquals(parameters[index].Value, values[index]) && (index == 3 || parameters[index].Alias is null);
                    bool copyback = fuel >= 69
                        ? ExactAvailableGas(output.Value) && ReferenceEquals(output.Value, observer.Policy!.Captures[1].Read())
                        : ReferenceEquals(output.Value, sentinel);
                    bool cacheExact = published == (cached || fuel >= 77) &&
                        machine._statics.Count == (published ? 2 : 1);
                    if (published)
                    {
                        cacheExact &= staticCell!.Alias is null && staticCell.Identity == "static:" + okSymbol &&
                            ExactAvailableOk(staticCell.Value) && !ReferenceEquals(staticCell, output) &&
                            ReferenceEquals(caller.Captures[0].Immediate, staticCell.Value) &&
                            (cached ? ReferenceEquals(staticCell, existing) : ReferenceEquals(staticCell.Value, observer.Initialized));
                    }
                    if (returned is not null)
                        cacheExact &= ReferenceEquals(returned, staticCell!.Value) && ExactAvailableOk(returned);
                    rows.Add(new()
                    {
                        ["fuel"] = fuel, ["cached"] = cached, ["bound"] = function.FuelBound,
                        ["exhausted"] = exhausted, ["returned"] = returned is not null, ["remaining"] = machine._fuel,
                        ["trace"] = string.Join(",", observer.Events),
                        ["preserved"] = preserved,
                        ["outAlias"] = observer.OutAlias && ReferenceEquals(parameters[3].Alias, outputLocation) &&
                            ReferenceEquals(outputLocation.Cell, output) && output.Alias is null,
                        ["parameters"] = observer.ParametersExact,
                        ["copyback"] = copyback,
                        ["fieldIdentity"] = observer.FieldsExact(),
                        ["publication"] = observer.PublicationExact,
                        ["cache"] = cacheExact,
                    });
                }
                return rows.ToArray();
            }

            private static bool ExactAvailableGas(StageBValue value)
            {
                if (value.Kind != StageBValueKind.Struct ||
                    value.Type != "global::Nethermind.Evm.GasPolicy.EthereumGasPolicy" || value.Fields is not { Count: 5 } ||
                    value.Field("Value").Kind != StageBValueKind.UInt64 || value.Field("Value").Type != "ulong") return false;
                foreach (string name in new[] { "StateReservoir", "StateGasUsed", "StateGasSpill", "StateGasSpillRefunded" })
                    if (value.Field(name).Kind != StageBValueKind.Int64 || value.Field(name).Type != "long") return false;
                return Gas(value) == new StageBGasValue(16_756_216, 3_219_784, 3_000, 0, 0);
            }

            private static bool ExactAvailableOk(StageBValue value) =>
                value.Kind == StageBValueKind.Struct &&
                value.Type == "global::Nethermind.Evm.TransactionProcessing.TransactionResult" &&
                value.Fields is { Count: 3 } &&
                value.Field("Error").Kind == StageBValueKind.Enum &&
                value.Field("Error").Type == "global::Nethermind.Evm.TransactionProcessing.TransactionResult.ErrorType" &&
                value.Field("Error").Signed == 0 &&
                value.Field("EvmExceptionType").Kind == StageBValueKind.Enum &&
                value.Field("EvmExceptionType").Type == "global::Nethermind.Evm.EvmExceptionType" &&
                value.Field("EvmExceptionType").Signed == 0 &&
                value.Field("ErrorDescription").Kind == StageBValueKind.Null &&
                value.Field("ErrorDescription").Type == "string";

            private sealed class AvailableGasProbe(
                Frame caller, Cell[] parameters, Cell output, StageBValue intrinsic, string okSymbol, long initialFuel, bool cached)
            {
                internal readonly List<string> Events = [];
                internal Frame? Policy;
                internal StageBValue? Initialized;
                internal bool OutAlias = true;
                internal bool ParametersExact = true;
                internal bool PublicationExact = true;
                private Cell[] _policyCells = [];
                private Location?[] _aliases = [];
                private Frame? _initializer;

                internal void Enter(Frame frame, long fuel, Dictionary<string, Cell> statics)
                {
                    Events.Add("enter:" + frame.Function.Signature.Name + ":" + (initialFuel - fuel));
                    if (frame.Function.Signature.Name == "TryCreateAvailableFromIntrinsic")
                    {
                        Policy = frame;
                        _policyCells = frame.Function.Signature.Parameters.Select(parameter => frame.Cells[parameter.Symbol]).ToArray();
                        _aliases = _policyCells.Select(static cell => cell.Alias).ToArray();
                        OutAlias &= _policyCells[3].Alias is CellLocation location &&
                            ReferenceEquals(location.Cell, parameters[3]) && ReferenceEquals(location.Resolve().Root, output) &&
                            !location.Resolve().ReadOnly;
                        ParametersExact &= !ReferenceEquals(frame, caller) && ReferenceEquals(frame.This.Immediate, StageBValue.Unit) &&
                            frame.This.Target is null && frame.Cells.Count == 4 && frame.Captures.Count == 0 &&
                            _policyCells[0].Alias is null && _policyCells[0].Value.Unsigned == 20_000_000 &&
                            _policyCells[1].Alias is ValueLocation temporary && temporary.Resolve().ReadOnly &&
                            temporary.Identity == "temporary:" + frame.Function.Signature.Parameters[1].Symbol &&
                            ReferenceEquals(temporary.Read(), intrinsic.Field("Standard")) &&
                            _policyCells[2].Alias is null && ReferenceEquals(_policyCells[2].Value, parameters[1].Value);
                        for (int index = 0; index < _policyCells.Length; index++)
                        {
                            ParametersExact &= !parameters.Any(parameter => ReferenceEquals(parameter, _policyCells[index]));
                            for (int earlier = 0; earlier < index; earlier++)
                                ParametersExact &= !ReferenceEquals(_policyCells[index], _policyCells[earlier]);
                        }
                    }
                    if (frame.Function.Signature.Name == "Ok")
                    {
                        _initializer = frame;
                        PublicationExact &= !cached && !statics.ContainsKey(okSymbol) && ExactAvailableGas(output.Value) &&
                            !ReferenceEquals(frame, caller) && !ReferenceEquals(frame, Policy) && frame.This.Target is null &&
                            frame.Cells.Count == 0 && frame.Captures.Count == 0 && ReferenceEquals(frame.This.Immediate, StageBValue.Unit);
                    }
                }

                internal void Return(Frame frame, StageBValue value, long fuel, Dictionary<string, Cell> statics)
                {
                    Events.Add("return:" + frame.Function.Signature.Name + ":" + (initialFuel - fuel));
                    if (ReferenceEquals(frame, Policy))
                    {
                        ParametersExact &= value.Kind == StageBValueKind.Bool && value.Boolean && frame.Cells.Count == 5;
                        for (int index = 0; index < _policyCells.Length; index++)
                            ParametersExact &= ReferenceEquals(frame.Cells[frame.Function.Signature.Parameters[index].Symbol], _policyCells[index]) &&
                                ReferenceEquals(_policyCells[index].Alias, _aliases[index]);
                        OutAlias &= ReferenceEquals(frame.Captures[0].Target!.Resolve().Root, output);
                    }
                    if (frame.Function.Signature.Name == "Ok")
                    {
                        Initialized = value;
                        PublicationExact &= ReferenceEquals(frame, _initializer) && frame.Cells.Count == 0 && frame.Captures.Count == 0 &&
                            !statics.ContainsKey(okSymbol) && ExactAvailableOk(value);
                    }
                    if (ReferenceEquals(frame, caller))
                        PublicationExact &= statics.TryGetValue(okSymbol, out Cell? cell) && ReferenceEquals(value, cell.Value);
                }

                internal bool FieldsExact()
                {
                    if (Policy is null || Policy.Captures.Count != 2) return false;
                    StageBValue result = Policy.Cells[Policy.Function.Bindings.Single().Symbol].Value;
                    StageBValue available = Policy.Captures[1].Read();
                    if (!ExactAvailableGas(available) ||
                        ReferenceEquals(result.Field("StateGasSpill"), result.Field("StateGasSpillRefunded"))) return false;
                    foreach (string name in new[] { "Value", "StateReservoir", "StateGasUsed", "StateGasSpill", "StateGasSpillRefunded" })
                        if (!ReferenceEquals(available.Field(name), result.Field(name))) return false;
                    return true;
                }
            }
            """;
        List<(string Original, string Replacement)> replacements =
        [
            ("private long _fuel;", "private long _fuel;\n" + probe),
            ("private StageBValue Execute(Frame frame)\n        {",
                "private StageBValue Execute(Frame frame)\n        {\n            _availableGasProbe?.Enter(frame, _fuel, _statics);"),
            ("if (result.Kind == StageBEdgeKind.Return) return result.Cursor.Carried;", """
                if (result.Kind == StageBEdgeKind.Return)
                {
                    _availableGasProbe?.Return(frame, result.Cursor.Carried, _fuel, _statics);
                    return result.Cursor.Carried;
                }
                """),
        ];
        switch (mutation)
        {
            case "out-copy":
                replacements.Add(("new(parameter.Symbol, Default(parameter.Type)) { Alias = location };",
                    "new(parameter.Symbol, Default(parameter.Type)) { Alias = parameter.RefKind == StageBRefKind.Out ? null : location };"));
                break;
            case "skip-copyback":
                replacements.Add(("try { target.Write(source.Read()); }",
                    "try { if (node.Children[0].Kind != StageBOperationKind.FlowCaptureReference) target.Write(source.Read()); }"));
                break;
            case "early-publication":
                replacements.Add((
                    "StageBValue value = Execute(new Frame(_functions[call.Target.Body], Operand.Value(StageBValue.Unit)));\n" +
                    "                    cell = new(\"static:\" + call.Target.Member.Symbol, value);\n" +
                    "                    _statics.Add(call.Target.Member.Symbol, cell);", """
                    cell = new("static:" + call.Target.Member.Symbol, StageBValue.Unit);
                    _statics.Add(call.Target.Member.Symbol, cell);
                    cell.Value = Execute(new Frame(_functions[call.Target.Body], Operand.Value(StageBValue.Unit)));
                    """));
                break;
            case "skip-terminal":
                replacements.Add(("Tick();\n                if (!blocks.TryGetValue", """
                    if (frame.Function.Signature.Name != "CalculateAvailableGas" || cursor.Ordinal != 5) Tick();
                                    if (!blocks.TryGetValue
                    """));
                break;
        }
        CSharpCompilation candidate = Mutate(StageBControlExtractor.InterpreterPath, replacements.ToArray());
        using MemoryStream assembly = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = candidate.Emit(assembly);
        Assert.That(emitted.Success, Is.True, string.Join('\n', emitted.Diagnostics));
        assembly.Position = 0;
        System.Runtime.Loader.AssemblyLoadContext context = new(null, isCollectible: true);
        try
        {
            System.Reflection.MethodInfo method = context.LoadFromStream(assembly)
                .GetType("Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBPrefixInterpreter+Machine")!
                .GetMethod("ProbeAvailableGas", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            Dictionary<string, object>[] rows = (Dictionary<string, object>[])method.Invoke(null, [_root, mutation == "projection"])!;
            Assert.That(rows, Has.Length.EqualTo(11));
            foreach (Dictionary<string, object> row in rows)
            {
                long fuel = (long)row["fuel"];
                bool cached = (bool)row["cached"];
                long cost = cached ? 78 : 80;
                List<string> expectedTrace = ["enter:CalculateAvailableGas:0", "enter:TryCreateAvailableFromIntrinsic:13"];
                if (fuel >= 72) expectedTrace.Add("return:TryCreateAvailableFromIntrinsic:72");
                if (!cached && fuel >= 75) expectedTrace.Add("enter:Ok:75");
                if (!cached && fuel >= 77) expectedTrace.Add("return:Ok:77");
                if (fuel >= cost) expectedTrace.Add("return:CalculateAvailableGas:" + cost);
                row["timing"] = (bool)row["returned"] == (fuel >= cost) &&
                    (bool)row["exhausted"] == (fuel < cost) &&
                    (long)row["remaining"] == Math.Max(0, fuel - cost) &&
                    (string)row["trace"] == string.Join(",", expectedTrace);
            }
            if (mutation != "baseline")
            {
                string failedObservation = mutation switch
                {
                    "out-copy" => "outAlias", "skip-copyback" => "copyback", "projection" => "fieldIdentity",
                    "early-publication" => "publication", "skip-terminal" => "timing",
                    _ => throw new AssertionException(mutation),
                };
                Assert.That(rows.Any(row => !(bool)row[failedObservation]), Is.True, mutation);
                if (mutation == "projection")
                    foreach (Dictionary<string, object> row in rows)
                        foreach (string observation in new[] { "copyback", "parameters", "cache", "timing" })
                            Assert.That(row[observation], Is.True, "The zero-valued projection swap must remain observationally equal: " + observation);
                return;
            }
            foreach (Dictionary<string, object> row in rows)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(row["bound"], Is.EqualTo(106L));
                    foreach (string observation in new[] { "preserved", "outAlias", "parameters", "copyback", "fieldIdentity", "publication", "cache", "timing" })
                        Assert.That(row[observation], Is.True, $"{observation}, fuel {row["fuel"]}, cached {row["cached"]}; {row["trace"]}");
                }
            }
        }
        finally { context.Unload(); }
    }


    [Test]
    public void Stage_b_string_literal_decoder_preserves_the_complete_canonical_payload()
    {
        const string probe = """
            private static object[] ProbeStringLiteral(string type, string constant)
            {
                try
                {
                    StageBValue value = Constant(type, constant);
                    return [(int)value.Kind, value.Type, value.Text!];
                }
                catch (StageBExecutionException exception) { return [exception.Code]; }
            }
            """;
        CSharpCompilation candidate = Mutate(StageBControlExtractor.InterpreterPath,
            ("private long _fuel;", "private long _fuel;\n" + probe));
        using MemoryStream assembly = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = candidate.Emit(assembly);
        Assert.That(emitted.Success, Is.True, string.Join('\n', emitted.Diagnostics));
        assembly.Position = 0;
        System.Runtime.Loader.AssemblyLoadContext context = new(null, isCollectible: true);
        try
        {
            System.Reflection.MethodInfo method = context.LoadFromStream(assembly)
                .GetType("Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBPrefixInterpreter+Machine")!
                .GetMethod("ProbeStringLiteral", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            object[] Decode(string type, string constant) => (object[])method.Invoke(null, [type, constant])!;
            object[] Prefix(string payload) => [(int)StageBValueKind.Reference, "string", payload];

            Assert.That(Decode("string", "string:intrinsic gas too low"), Is.EqualTo(Prefix("intrinsic gas too low")));
            Assert.That(Decode("string", "string:"), Is.EqualTo(Prefix("")));
            Assert.That(Decode("string", "string:left:right"), Is.EqualTo(Prefix("left:right")));
            Assert.That(Decode("not-string", "string:value"), Is.EqualTo(new object[] { "constant" }));
            Assert.That(Decode("string", "System.String:value"), Is.EqualTo(new object[] { "constant" }));
        }
        finally { context.Unload(); }
    }

    [Test]
    public void Stage_b_int_model_preserves_byte_conversion_and_typed_zero_default()
    {
        const string probe = """
            private static object[] ProbeIntModel(long input)
            {
                StageBValue source = StageBValue.Enum("byte", input);
                StageBValue converted = ConvertValue(source, "int");
                StageBValue defaulted = Default("int");
                StageBValue constant = Constant("int", "");
                return [(int)converted.Kind, converted.Type, converted.Signed,
                    (int)defaulted.Kind, defaulted.Type, defaulted.Signed,
                    (int)constant.Kind, constant.Type, constant.Signed,
                    source.Type, source.Signed];
            }
            """;
        CSharpCompilation candidate = Mutate(StageBControlExtractor.InterpreterPath,
            ("private long _fuel;", "private long _fuel;\n" + probe));
        using MemoryStream assembly = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = candidate.Emit(assembly);
        Assert.That(emitted.Success, Is.True, string.Join('\n', emitted.Diagnostics));
        assembly.Position = 0;
        System.Runtime.Loader.AssemblyLoadContext context = new(null, isCollectible: true);
        try
        {
            System.Reflection.MethodInfo method = context.LoadFromStream(assembly)
                .GetType("Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBPrefixInterpreter+Machine")!
                .GetMethod("ProbeIntModel", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            for (long input = byte.MinValue; input <= byte.MaxValue; input++)
            {
                object[] actual = (object[])method.Invoke(null, [input])!;
                Assert.That(actual, Is.EqualTo(new object[]
                {
                    (int)StageBValueKind.Enum, "int", input,
                    (int)StageBValueKind.Enum, "int", 0L,
                    (int)StageBValueKind.Enum, "int", 0L,
                    "byte", input,
                }), $"byte {input}");
            }
        }
        finally { context.Unload(); }
    }

    [TestCase("")]
    [TestCase("reversed-children")]
    [TestCase("reversed-evaluation-order")]
    [TestCase("wrong-argument-index")]
    [TestCase("early-receiver-read")]
    [TestCase("skipped-callee")]
    [TestCase("wrong-callee-frame")]
    [TestCase("copied-constructor-location")]
    public void Stage_b_control_local_call_executes_child_before_return_and_caller_continuation(string mutation)
    {
        const string probe = """
            private static object[] ProbeRecursiveCall(bool constructing, int mode, string scenario, long fuel)
            {
                List<string> events = [];
                RecursiveCallLocation receiver = new("receiver", 7, events, scenario != "receiver-value");
                RecursiveCallLocation first = new("first", 10, events, false);
                RecursiveCallLocation second = new("second", 20, events, scenario == "argument-poison");
                RecursiveCallLocation reference = new("reference", 5, events, false);
                RecursiveCallLocation output = new("output", -1, events, false);
                RecursiveCallLocation continuation = new("continuation", -1, events, false);
                StageBTermBinding binding = new(-1, StageBRefKind.None, false, StageBArgumentMode.Value, null, []);
                StageBPrefixNode Node(StageBOperationKind kind, string symbol = "", StageBPrefixOperandMode operandMode = StageBPrefixOperandMode.Value,
                    params StageBPrefixNode[] children) => new(kind, "long", symbol, "", "", "", false, operandMode, binding, null, [], children, true);
                StageBPrefixNode Literal(long value) => Node(StageBOperationKind.Literal) with { Constant = "System.Int64:" + value };
                StageBPrefixNode Parameter(string symbol, StageBPrefixOperandMode operandMode) => Node(StageBOperationKind.ParameterReference, symbol, operandMode);
                StageBPrefixNode Assign(StageBPrefixNode target, StageBPrefixNode source) => Node(StageBOperationKind.SimpleAssignment, children: [target, source]);
                StageBParameter[] parameters =
                [
                    new("p", "p", 0, "long", StageBRefKind.None, false),
                    new("q", "q", 1, "long", StageBRefKind.None, false),
                    new("r", "r", 2, "long", StageBRefKind.Ref, false),
                ];
                StageBMember member = new("callee", "", "Callee", "long", StageBMemberKind.Method, StageBReceiverKind.Value,
                    "long", StageBRefKind.None, parameters);
                StageBPrefixNode thisNode = Node(StageBOperationKind.InstanceReference, operandMode: StageBPrefixOperandMode.Location) with
                    { Operator = "ContainingTypeInstance" };
                StageBPrefixNode childBranch = scenario switch
                {
                    "rejected" => Node(StageBOperationKind.LocalReference, "missing"),
                    "suspend" => Node(StageBOperationKind.Invocation) with
                        { Call = new(new(StageBPrefixTargetKind.RefundSuspension, member, "", null), -1, []) },
                    _ => Literal(71),
                };
                StageBPrefixNode receiverWrite = scenario == "receiver-alias"
                    ? Assign(thisNode, Parameter("r", StageBPrefixOperandMode.Location)) with { Binding = binding with { IsRef = true } }
                    : Assign(thisNode, Literal(37));
                StageBPrefixBlock childBlock = new(0,
                    [Assign(Parameter("r", StageBPrefixOperandMode.Location), Parameter("p", StageBPrefixOperandMode.Value)),
                     Assign(Parameter("r", StageBPrefixOperandMode.Location), Parameter("q", StageBPrefixOperandMode.Value)),
                     Assign(Parameter("r", StageBPrefixOperandMode.Location), Literal(53)), receiverWrite],
                    childBranch, "None", scenario == "outside" ? StageBPrefixExit.OutsideSelectedDomain : StageBPrefixExit.Return, null, null);
                StageBPrefixFunction child = new(member, new(0, 0), [], [], [], [], [childBlock], [], [], [], 28, true);
                StageBPrefixNode invocation = Node(constructing ? StageBOperationKind.ObjectCreation : StageBOperationKind.Invocation,
                    operandMode: (StageBPrefixOperandMode)mode, children:
                    [Node(StageBOperationKind.LocalReference, "receiver", scenario is "receiver-value" or "receiver-poison"
                        ? StageBPrefixOperandMode.Value : StageBPrefixOperandMode.Location),
                     Node(StageBOperationKind.LocalReference, "first"),
                     Node(StageBOperationKind.LocalReference, "second", StageBPrefixOperandMode.Location),
                     Node(StageBOperationKind.LocalReference, "reference", StageBPrefixOperandMode.Location)]) with
                {
                    Call = new(new(StageBPrefixTargetKind.Local, member, "callee", null), 0,
                        [new(2, 0, StageBArgumentMode.Value, false, "Explicit"), new(1, 1, StageBArgumentMode.Value, false, "Explicit"),
                         new(3, 2, StageBArgumentMode.WritableLocation, false, "Explicit")]),
                };
                StageBPrefixNode capture = Node(StageBOperationKind.FlowCapture, children: [invocation]) with { Binding = binding with { Capture = 0 } };
                StageBPrefixNode captured = Node(StageBOperationKind.FlowCaptureReference) with { Binding = binding with { Capture = 0 } };
                StageBPrefixBlock rootBlock = new(0,
                    [capture, Assign(Node(StageBOperationKind.LocalReference, "output", StageBPrefixOperandMode.Location), captured),
                     Assign(Node(StageBOperationKind.LocalReference, "continuation", StageBPrefixOperandMode.Location), Literal(97))],
                    Literal(113), "None", StageBPrefixExit.Return, null, null);
                StageBPrefixFunction root = new(member with { Symbol = "root", Parameters = [] }, new(0, 0), [], [], [], [], [rootBlock], [],
                    [new(0, StageBPrefixOperandMode.Location)], [], 28, true);
                StageBPrefixProgram program = new("root", "", [root, child], [], [], [], null!, [], 28, "", [], [], [], [], "");
                Machine machine = new(program, null!, new(), fuel);
                Frame frame = new(root, Operand.Value(StageBValue.Unit));
                foreach (RecursiveCallLocation location in new[] { receiver, first, second, reference, output, continuation })
                    frame.Cells[location.Identity] = new(location.Identity, StageBValue.Int64(0)) { Alias = location };
                string outcome = "ok";
                long result = -1;
                try { result = machine.Execute(frame).Signed; }
                catch (StageBExecutionException exception) { outcome = exception.Code; }
                catch (FuelSignal) { outcome = "fuel"; }
                catch (OutsideSignal) { outcome = "outside"; }
                catch (SuspendSignal) { outcome = "suspend"; }
                string captureKind = "missing";
                long captureValue = -1;
                string identity = "";
                string write = "skipped";
                if (frame.Captures.TryGetValue(0, out Operand value))
                {
                    captureKind = value.Target is null ? "value" : "location";
                    captureValue = value.Read().Signed;
                    identity = value.Target?.Identity ?? "";
                    try { value.Write(StageBValue.Int64(89)); write = value.Read().Signed == 89 ? "ok" : "lost"; }
                    catch (StageBExecutionException exception) { write = exception.Code; }
                }
                return [outcome, result, captureKind, captureValue, identity, write, receiver.Value.Signed, reference.Value.Signed,
                    output.Value.Signed, continuation.Value.Signed, machine._fuel, string.Join("|", events)];
            }

            private sealed class RecursiveCallLocation(string identity, long value, List<string> events, bool poison) : Location(false)
            {
                internal StageBValue Value = StageBValue.Int64(value);
                internal override string Identity => identity;
                internal override StageBValue Read()
                {
                    events.Add(identity + ":read:" + Value.Signed);
                    if (poison) throw new StageBExecutionException("poison-read", identity);
                    return Value;
                }
                internal override void Write(StageBValue value)
                {
                    events.Add(identity + ":write:" + value.Signed);
                    Value = value;
                }
                internal override StageBResolvedLocation Resolve() => new(this, [], false, Identity);
            }
            """;
        List<(string Original, string Replacement)> changes = [];
        if (mutation.Length != 0)
        {
            changes.Add(mutation switch
            {
                "reversed-children" => ("operands[index] = Evaluate(frame, node.Children[index]);", "operands[index] = Evaluate(frame, node.Children[node.Children.Length - 1 - index]);"),
                "reversed-evaluation-order" => ("for (int index = 0; index < node.Children.Length; index++) operands[index] = Evaluate(frame, node.Children[index]);", "for (int index = node.Children.Length - 1; index >= 0; index--) operands[index] = Evaluate(frame, node.Children[index]);"),
                "wrong-argument-index" => ("Operand actual = operands[argument.Child];", "Operand actual = operands[argument.Ordinal];"),
                "early-receiver-read" => ("StageBValue result = Execute(frame);", "_ = receiver.Read(); StageBValue result = Execute(frame);"),
                "skipped-callee" => ("StageBValue result = Execute(frame);", "StageBValue result = StageBValue.Unit;"),
                "wrong-callee-frame" => ("StageBValue result = Execute(frame);", "StageBValue result = Execute(caller);"),
                "copied-constructor-location" => ("return returnKind == StageBLocalReturnKind.ReceiverValue ? Operand.Value(receiver.Read()) : receiver;", "return returnKind == StageBLocalReturnKind.ReceiverValue ? Operand.Value(receiver.Read()) : Operand.Location(new CellLocation(new Cell(\"new:\" + call.Target.Member.Symbol, receiver.Read()), false));"),
                _ => throw new AssertionException(mutation),
            });
            CSharpCompilation mutant = Mutate(StageBControlExtractor.InterpreterPath, changes.ToArray());
            Assert.That(() => StageBControlExtractor.Generate(mutant), Throws.TypeOf<ExtractionException>(), mutation);
        }
        changes.Add(("private long _fuel;", "private long _fuel;\n" + probe));
        CSharpCompilation candidate = Mutate(StageBControlExtractor.InterpreterPath, changes.ToArray());
        using MemoryStream assembly = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = candidate.Emit(assembly);
        Assert.That(emitted.Success, Is.True, string.Join('\n', emitted.Diagnostics));
        assembly.Position = 0;
        System.Runtime.Loader.AssemblyLoadContext context = new(null, isCollectible: true);
        try
        {
            System.Reflection.Assembly loaded = context.LoadFromStream(assembly);
            System.Reflection.MethodInfo method = loaded.GetType("Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBPrefixInterpreter+Machine")!
                .GetMethod("ProbeRecursiveCall", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            object[] Run(bool constructing, int mode, string scenario = "normal", long fuel = 28) =>
                (object[])method.Invoke(null, [constructing, mode, scenario, fuel])!;
            const string reads = "first:read:10|second:read:20";
            const string childWrites = "|reference:write:20|reference:write:10|reference:write:53";
            object[] receiverEvaluation =
            [
                "ok", 113L, "value", 37L, "", "location", 7L, 53L, 37L, 97L, 0L,
                "receiver:read:7|" + reads + childWrites + "|output:write:37|continuation:write:97",
            ];
            object[] receiverAlias =
            [
                "ok", 113L, "location", 53L, "reference", "ok", 7L, 89L, 53L, 97L, 0L,
                reads + childWrites + "|reference:read:53|reference:read:53|reference:read:53|output:write:53|reference:read:53" +
                    "|continuation:write:97|reference:read:53|reference:write:89|reference:read:89",
            ];
            if (mutation.Length != 0)
            {
                if (mutation == "reversed-evaluation-order")
                {
                    Assert.That(Run(true, 0, "receiver-value"), Is.Not.EqualTo(receiverEvaluation), "Operand indexes are unchanged; evaluation order must still be observable.");
                    return;
                }
                if (mutation == "copied-constructor-location")
                {
                    Assert.That(Run(true, 1, "receiver-alias"), Is.Not.EqualTo(receiverAlias), "A copied location must not satisfy receiver alias identity.");
                    return;
                }
                Assert.That(Run(false, 0), Is.Not.EqualTo(new object[]
                {
                    "ok", 113L, "value", 71L, "", "location", 37L, 53L, 71L, 97L, 0L,
                    reads + childWrites + "|receiver:write:37|output:write:71|continuation:write:97",
                }), "The same independent normal-path observation must detect " + mutation);
                return;
            }
            foreach (bool constructing in new[] { false, true })
            foreach (int mode in new[] { 0, 1, 2 })
            {
                bool location = constructing && mode != 0;
                long selected = constructing ? 37 : 71;
                Assert.That(Run(constructing, mode), Is.EqualTo(new object[]
                {
                    "ok", 113L, location ? "location" : "value", selected, location ? "new:callee" : "", location ? "ok" : "location",
                    constructing ? 7L : 37L, 53L, selected, 97L, 0L,
                    reads + childWrites + (constructing ? "" : "|receiver:write:37") + $"|output:write:{selected}|continuation:write:97",
                }), $"constructing={constructing}/mode={mode}");
            }
            Assert.That(Run(true, 0, "receiver-value"), Is.EqualTo(receiverEvaluation), "Even an ignored constructor receiver child evaluates before arguments.");
            foreach (int mode in new[] { 1, 2 })
                Assert.That(Run(true, mode, "receiver-alias"), Is.EqualTo(receiverAlias), "The returned location must retain the receiver's actual alias graph.");
            foreach ((string scenario, string outcome, long remaining, long referenceValue, string events) in new[]
            {
                ("receiver-poison", "poison-read", 24L, 5L, "receiver:read:7"),
                ("argument-poison", "poison-read", 21L, 5L, reads),
                ("outside", "outside", 20L, 5L, reads),
                ("rejected", "uninitialized-binding", 7L, 53L, reads + childWrites),
                ("suspend", "suspend", 7L, 53L, reads + childWrites),
            })
                Assert.That(Run(true, 0, scenario), Is.EqualTo(new object[]
                { outcome, -1L, "missing", -1L, "", "skipped", 7L, referenceValue, -1L, -1L, remaining, events }), scenario);
            foreach ((long fuel, long referenceValue, string events) in new[]
            {
                (0L, 5L, ""), (7L, 5L, reads), (20L, 53L, reads + childWrites),
            })
                Assert.That(Run(true, 0, fuel: fuel), Is.EqualTo(new object[]
                { "fuel", -1L, "missing", -1L, "", "skipped", 7L, referenceValue, -1L, -1L, 0L, events }), $"Shared fuel {fuel}");
            Assert.That(Run(true, 0, fuel: 27), Is.EqualTo(new object[]
            {
                "fuel", -1L, "value", 37L, "", "location", 7L, 53L, 37L, 97L, 0L,
                reads + childWrites + "|output:write:37|continuation:write:97",
            }), "One-short fuel fails the caller's terminal branch after prior continuation effects.");
        }
        finally { context.Unload(); }
    }

    [Test]
    public void Stage_b_control_edges_match_independent_optional_edge_table()
    {
        (int Destination, bool Returns)?[] edges = [null, (-1, false), (-1, true), (0, false), (int.MaxValue, true)];
        foreach (string condition in new[] { "None", "WhenTrue", "WhenFalse", "unknown" })
        foreach (bool isBoolean in new[] { false, true })
        foreach (bool value in new[] { false, true })
        foreach ((int Destination, bool Returns)? fallThrough in edges)
        foreach ((int Destination, bool Returns)? conditional in edges)
        {
            StageBEdgeResult expected;
            if (condition != "None" && !isBoolean) expected = new(StageBEdgeKind.InvalidCondition, -1);
            else
            {
                (int Destination, bool Returns)? selected = condition switch
                {
                    "WhenTrue" when value => conditional ?? fallThrough,
                    "WhenFalse" when !value => conditional ?? fallThrough,
                    _ => fallThrough,
                };
                expected = selected switch
                {
                    null => new(StageBEdgeKind.MissingEdge, -1),
                    { Destination: < 0, Returns: true } => new(StageBEdgeKind.Return, -1),
                    { Destination: < 0 } => new(StageBEdgeKind.InvalidEdge, -1),
                    { } edge => new(StageBEdgeKind.Jump, edge.Destination),
                };
            }
            object carried = new();
            StageBFinishResult<object> finished = StageBControlKernel.FinishBlock(StageBPrefixExit.Branch,
                new StageBBlockCursor<object>(83, carried), condition, isBoolean, value,
                fallThrough.HasValue, fallThrough?.Destination ?? -1, fallThrough?.Returns ?? false,
                conditional.HasValue, conditional?.Destination ?? -1, conditional?.Returns ?? false);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(StageBControlKernel.SelectEdge(condition, isBoolean, value,
                    fallThrough.HasValue, fallThrough?.Destination ?? -1, fallThrough?.Returns ?? false,
                    conditional.HasValue, conditional?.Destination ?? -1, conditional?.Returns ?? false), Is.EqualTo(expected));
                Assert.That(finished.Kind, Is.EqualTo(expected.Kind));
                Assert.That(finished.Cursor.Ordinal, Is.EqualTo(expected.Kind == StageBEdgeKind.Jump ? expected.Destination : 83));
                Assert.That(finished.Cursor.Carried, Is.SameAs(carried));
            }
        }
    }

    [Test]
    public void Stage_b_control_finish_direct_exit_precedes_invalid_condition_and_missing_edges(
        [Values] bool returns,
        [Values("None", "WhenTrue", "WhenFalse")] string condition,
        [Values] bool isBoolean,
        [Values] bool boolean)
    {
        StageBPrefixExit exit = returns ? StageBPrefixExit.Return : StageBPrefixExit.Suspend;
        StageBValue carried = StageBValue.Struct("asymmetric", ("left", StageBValue.UInt64(19)), ("right", StageBValue.UInt64(71)));
        StageBFinishResult<StageBValue> result = StageBControlKernel.FinishBlock(exit, new StageBBlockCursor<StageBValue>(83, carried),
            condition, isBoolean, boolean, false, -7, false, false, -11, false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Kind, Is.EqualTo(exit == StageBPrefixExit.Return ? StageBEdgeKind.Return : StageBEdgeKind.MissingSuspension));
            Assert.That(result.Cursor.Ordinal, Is.EqualTo(83));
            Assert.That(result.Cursor.Carried, Is.SameAs(carried));
        }
    }

    [TestCase("remainingFuel == 0", "remainingFuel < 0")]
    [TestCase("remainingFuel - 1", "remainingFuel - 2")]
    [TestCase("!valueIsBoolean", "valueIsBoolean")]
    [TestCase("conditionKind == \"WhenTrue\" && booleanValue", "conditionKind == \"WhenTrue\" && !booleanValue")]
    [TestCase("takeConditional ? conditionalDestination : fallThroughDestination", "takeConditional ? fallThroughDestination : conditionalDestination")]
    [TestCase("destination < 0", "destination < 1")]
    [TestCase("returns ? StageBEdgeKind.Return : StageBEdgeKind.InvalidEdge", "returns ? StageBEdgeKind.InvalidEdge : StageBEdgeKind.Return")]
    [TestCase("if (exit == StageBPrefixExit.Return)", "if (exit == StageBPrefixExit.Suspend)")]
    [TestCase("return new(StageBEdgeKind.MissingSuspension, cursor)", "return new(StageBEdgeKind.Return, cursor)")]
    [TestCase("new(edge.Destination, cursor.Carried)", "new(cursor.Ordinal, cursor.Carried)")]
    [TestCase("new(edge.Destination, cursor.Carried) : cursor", "new(edge.Destination, cursor.Carried) : new(-1, cursor.Carried)")]
    [TestCase("SelectEdge(conditionKind, valueIsBoolean, booleanValue,", "SelectEdge(conditionKind, valueIsBoolean, !booleanValue,")]
    [TestCase("if (!constructing)", "if (constructing)")]
    [TestCase("valueMode ? StageBLocalReturnKind.ReceiverValue : StageBLocalReturnKind.ReceiverLocation", "valueMode ? StageBLocalReturnKind.ReceiverLocation : StageBLocalReturnKind.ReceiverValue")]
    [TestCase("!preservesOperand && valueMode", "preservesOperand && valueMode")]
    [TestCase("!preservesOperand && valueMode", "!preservesOperand && !valueMode")]
    public async Task Stage_b_control_semantic_mutation_changes_extraction_and_fails_independent_proof(string original, string replacement)
    {
        CSharpCompilation mutant = Mutate(StageBControlExtractor.KernelPath, (original, replacement));
        StageBControlDocument changed = StageBControlExtractor.Generate(mutant);
        string emitted = StageBControlExtractor.Emit(changed);
        Assert.That(emitted, Is.Not.EqualTo(StageBControlExtractor.Emit(StageBControlExtractor.Generate(_inputs.Compilation))));
        (int exit, string output) = await CheckProof(emitted);
        Assert.That(exit, Is.Not.Zero, output);
        Assert.That(output, Does.Contain("error:"), output);
    }

    [TestCase("remainingFuel - 1", "unchecked(-long.MinValue)", "Unsupported control expression")]
    [TestCase("remainingFuel - 1", "-remainingFuel", "Unsupported control expression")]
    [TestCase("if (remainingFuel == 0)", "long minimum = -9223372036854775807 - 1; if (-minimum < 0) return new(true, 0); if (remainingFuel == 0)", "Unsupported control expression")]
    [TestCase("if (remainingFuel == 0)", "TypedReference alias = __makeref(remainingFuel); __refvalue(alias, long) = long.MaxValue; if (remainingFuel == 0)", "Control TypedReference intrinsics are unsupported")]
    [TestCase("remainingFuel - 1", "Math.Abs(remainingFuel)", "Unsupported control expression")]
    [TestCase("new(true, 0)", "new(RemainingFuel: 0, Exhausted: true)", "Unsupported control expression")]
    [TestCase("Jump, Return, InvalidCondition", "Jump = 1, Return, InvalidCondition", "Edge result tags changed")]
    [TestCase("ReturnedValue, ReceiverValue, ReceiverLocation", "ReceiverValue, ReturnedValue, ReceiverLocation", "Local return tags changed")]
    [TestCase("record struct StageBTickResult(bool Exhausted, long RemainingFuel);", "record struct StageBTickResult(bool Exhausted, long RemainingFuel) { public long Hidden => RemainingFuel; }", "Control result representation changed")]
    [TestCase("record struct StageBBlockCursor<T>(int Ordinal, T Carried);", "record struct StageBBlockCursor<T>(int Ordinal, T Carried) { public T Carried { get; init; } = default!; }", "Control result representation changed")]
    [TestCase("record struct StageBFinishResult<T>(StageBEdgeKind Kind, StageBBlockCursor<T> Cursor);", "record struct StageBFinishResult<T>(StageBEdgeKind Kind, StageBBlockCursor<T> Cursor) { public StageBBlockCursor<T> Cursor { get; init; } = new(-1, Cursor.Carried); }", "Control result representation changed")]
    [TestCase("new(edge.Destination, cursor.Carried)", "new(edge.Destination, default(T)!)", "Unsupported control expression")]
    [TestCase("if (exit == StageBPrefixExit.Return)", "object? inspected = cursor.Carried; if (exit == StageBPrefixExit.Return)", "Unsupported control expression")]
    [TestCase("if (exit == StageBPrefixExit.Return)", "string? inspected = cursor.Carried!.ToString(); if (exit == StageBPrefixExit.Return)", "Unsupported control expression")]
    [TestCase("hasConditional, conditionalDestination, conditionalReturns);", "hasConditional, conditionalReturns: conditionalReturns, conditionalDestination: conditionalDestination);", "Unsupported control expression")]
    [TestCase("internal static bool ShouldReadTransparent", "[Obsolete] internal static bool ShouldReadTransparent", "Unsupported control root signature")]
    public void Stage_b_control_rejects_compile_valid_unadmitted_kernel_mutations(string original, string replacement, string gate)
    {
        CSharpCompilation mutant = Mutate(StageBControlExtractor.KernelPath, (original, replacement));
        Assert.That(() => StageBControlExtractor.Generate(mutant), Throws.TypeOf<ExtractionException>().With.Message.Contains(gate));
    }

    [TestCase("StageBControlKernel.Tick(_fuel)", "StageBControlKernel.Tick(1)", "Control Tick adapter changed")]
    [TestCase("StageBControlKernel.SelectLocalReturn(constructing, node.Mode == StageBPrefixOperandMode.Value)", "StageBControlKernel.SelectLocalReturn(!constructing, node.Mode == StageBPrefixOperandMode.Value)", "Control local return adapter changed")]
    [TestCase("StageBValue result = Execute(frame);", "StageBValue result = Execute(frame); _ = receiver.Read();", "Control local return adapter changed")]
    [TestCase("StageBValue result = Execute(frame);", "StageBValue result = StageBValue.Unit;", "Control local return adapter changed")]
    [TestCase("if (returnKind == StageBLocalReturnKind.ReturnedValue) return Operand.Value(result);", "if (returnKind == StageBLocalReturnKind.ReturnedValue) return receiver;", "Control local return adapter changed")]
    [TestCase("return returnKind == StageBLocalReturnKind.ReceiverValue ? Operand.Value(receiver.Read()) : receiver;", "return returnKind == StageBLocalReturnKind.ReceiverValue ? receiver : Operand.Value(receiver.Read());", "Control local return adapter changed")]
    [TestCase("private long _fuel;", "private long _fuel; private readonly record struct StageBLocalReturnKind(int Tag) { internal static StageBLocalReturnKind ReturnedValue => new(1); internal static StageBLocalReturnKind ReceiverValue => new(1); public static implicit operator StageBLocalReturnKind(global::Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBLocalReturnKind value) => new((int)value); }", "Control local return type rebound")]
    [TestCase("Operand operand = Evaluate(frame, node.Children[0]);", "Operand operand = Evaluate(frame, node.Children[node.Children.Length - 1]);", "Control transparent adapter changed")]
    [TestCase("Operand operand = Evaluate(frame, node.Children[0]);", "Operand operand = Evaluate(frame, node.Children[0]); _ = Evaluate(frame, node.Children[0]);", "Control transparent adapter changed")]
    [TestCase("if (node.Children.Length != 1) throw new StageBExecutionException(\"transparent-arity\", node.Kind.ToString());\n            Operand operand = Evaluate(frame, node.Children[0]);", "Operand operand = Evaluate(frame, node.Children[0]);\n            if (node.Children.Length != 1) throw new StageBExecutionException(\"transparent-arity\", node.Kind.ToString());", "Control transparent adapter changed")]
    [TestCase("StageBControlKernel.ShouldReadTransparent(preservesOperand, node.Mode == StageBPrefixOperandMode.Value)", "!preservesOperand && node.Mode == StageBPrefixOperandMode.Value", "Control transparent adapter changed")]
    [TestCase("StageBControlKernel.ShouldReadTransparent(preservesOperand, node.Mode == StageBPrefixOperandMode.Value)", "StageBControlKernel.ShouldReadTransparent(!preservesOperand, node.Mode == StageBPrefixOperandMode.Value)", "Control transparent adapter changed")]
    [TestCase("return StageBControlKernel.ShouldReadTransparent(preservesOperand, node.Mode == StageBPrefixOperandMode.Value)\n                ? Operand.Value(operand.Read()) : operand;", "return StageBControlKernel.ShouldReadTransparent(preservesOperand, node.Mode == StageBPrefixOperandMode.Value)\n                ? operand : operand;", "Control transparent adapter changed")]
    [TestCase("return StageBControlKernel.ShouldReadTransparent(preservesOperand, node.Mode == StageBPrefixOperandMode.Value)", "_ = operand.Read();\n            return StageBControlKernel.ShouldReadTransparent(preservesOperand, node.Mode == StageBPrefixOperandMode.Value)", "Control transparent adapter changed")]
    [TestCase("if (node.Kind == StageBOperationKind.Conversion && node.Call is { } call)\n                return Call(frame, node, call, [operand], constructing: false);\n            if (node.Kind == StageBOperationKind.Conversion && node.Mode == StageBPrefixOperandMode.Value)\n                return Operand.Value(ConvertValue(operand.Read(), node.Type));", "if (node.Kind == StageBOperationKind.Conversion && node.Mode == StageBPrefixOperandMode.Value)\n                return Operand.Value(ConvertValue(operand.Read(), node.Type));\n            if (node.Kind == StageBOperationKind.Conversion && node.Call is { } call)\n                return Call(frame, node, call, [operand], constructing: false);", "Control transparent adapter changed")]
    [TestCase("StageBOperationKind.ExpressionStatement or StageBOperationKind.Parenthesized or StageBOperationKind.Argument or\n                    StageBOperationKind.DeclarationExpression or StageBOperationKind.Conversion => EvaluateTransparent(frame, node),", "StageBOperationKind.ExpressionStatement or StageBOperationKind.Parenthesized or\n                    StageBOperationKind.DeclarationExpression or StageBOperationKind.Conversion => EvaluateTransparent(frame, node),", "Control transparent dispatch changed")]
    [TestCase("Tick();\n            return node.Kind", "return node.Kind", "Evaluation bypasses the control tick")]
    [TestCase("if (result.Exhausted) throw new FuelSignal();", "if (false) throw new FuelSignal();", "Control Tick adapter changed")]
    [TestCase("_fuel = result.RemainingFuel;", "_fuel = 1;", "Control Tick adapter changed")]
    [TestCase("_fuel = result.RemainingFuel;", "", "Control Tick adapter changed")]
    [TestCase("_fuel = result.RemainingFuel;", "_fuel = result.RemainingFuel; Console.WriteLine(_fuel);", "Control Tick adapter changed")]
    [TestCase("Tick();\n                if (!blocks.TryGetValue", "if (!blocks.TryGetValue", "Control edge adapter changed")]
    [TestCase("Tick();\n                if (!blocks.TryGetValue", "Tick();\n                Tick();\n                if (!blocks.TryGetValue", "Control edge adapter changed")]
    [TestCase("Tick();\n                if (!blocks.TryGetValue(cursor.Ordinal, out StageBPrefixBlock? block)) throw new StageBExecutionException(\"cfg-block\", cursor.Ordinal.ToString(CultureInfo.InvariantCulture));", "if (!blocks.TryGetValue(cursor.Ordinal, out StageBPrefixBlock? block)) throw new StageBExecutionException(\"cfg-block\", cursor.Ordinal.ToString(CultureInfo.InvariantCulture));\n                Tick();", "Control edge adapter changed")]
    [TestCase("new(frame.Function.Entry.Block, StageBValue.Unit)", "new(frame.Function.Entry.Operation, StageBValue.Unit)", "Control edge adapter changed")]
    [TestCase("blocks.TryGetValue(cursor.Ordinal, out", "blocks.TryGetValue(frame.Function.Entry.Block, out", "Control edge adapter changed")]
    [TestCase("StageBValue last = cursor.Carried;", "StageBValue last = StageBValue.Unit;", "Control edge adapter changed")]
    [TestCase("if (block.Exit == StageBPrefixExit.OutsideSelectedDomain) throw new OutsideSignal();\n                StageBValue last = cursor.Carried;\n                foreach (StageBPrefixNode operation in block.Operations) last = Evaluate(frame, operation).Read();", "StageBValue last = cursor.Carried;\n                foreach (StageBPrefixNode operation in block.Operations) last = Evaluate(frame, operation).Read();\n                if (block.Exit == StageBPrefixExit.OutsideSelectedDomain) throw new OutsideSignal();", "Control edge adapter changed")]
    [TestCase("foreach (StageBPrefixNode operation in block.Operations) last = Evaluate(frame, operation).Read();", "foreach (StageBPrefixNode operation in block.Operations.Reverse()) last = Evaluate(frame, operation).Read();", "Control edge adapter changed")]
    [TestCase("if (block.BranchValue is { } branch) last = Evaluate(frame, branch).Read();", "", "Control edge adapter changed")]
    [TestCase("StageBControlKernel.FinishBlock(block.Exit,", "StageBControlKernel.FinishBlock(StageBPrefixExit.Branch,", "Control edge adapter changed")]
    [TestCase("cursor = result.Cursor;", "cursor = new(0, result.Cursor.Carried);", "Control edge adapter changed")]
    [TestCase("cursor = result.Cursor;", "cursor = new(result.Cursor.Ordinal, StageBValue.Unit);", "Control edge adapter changed")]
    [TestCase("cursor = result.Cursor;", "", "Control edge adapter changed")]
    [TestCase("cursor = result.Cursor;", "cursor = new(cursor.Ordinal, last);", "Control edge adapter changed")]
    [TestCase("cursor = result.Cursor;", "cursor = result.Cursor; _requests.Clear();", "Control edge adapter changed")]
    [TestCase("new StageBBlockCursor<StageBValue>(cursor.Ordinal, last)", "new StageBBlockCursor<StageBValue>(cursor.Ordinal, cursor.Carried)", "Control edge adapter changed")]
    [TestCase("foreach (StageBPrefixNode operation in block.Operations) last = Evaluate(frame, operation).Read();", "foreach (StageBPrefixNode operation in block.Operations) _ = Evaluate(frame, operation).Read();", "Control edge adapter changed")]
    [TestCase("if (block.BranchValue is { } branch) last = Evaluate(frame, branch).Read();", "if (block.BranchValue is { } branch) _ = Evaluate(frame, branch).Read();", "Control edge adapter changed")]
    [TestCase("foreach (StageBPrefixNode operation in block.Operations) last = Evaluate(frame, operation).Read();", "foreach (StageBPrefixNode operation in block.Operations) last = Evaluate(frame, operation).Immediate ?? StageBValue.Unit;", "Control edge adapter changed")]
    [TestCase("foreach (StageBPrefixNode operation in block.Operations) last = Evaluate(frame, operation).Read();", "foreach (StageBPrefixNode operation in block.Operations) { Operand operand = Evaluate(frame, operation); last = operand.Read(); _ = operand.Read(); }", "Control edge adapter changed")]
    [TestCase("foreach (StageBPrefixNode operation in block.Operations) last = Evaluate(frame, operation).Read();", "foreach (StageBPrefixNode operation in block.Operations) { _ = Evaluate(frame, operation).Read(); last = cursor.Carried; }", "Control edge adapter changed")]
    [TestCase("foreach (StageBPrefixNode operation in block.Operations) last = Evaluate(frame, operation).Read();\n                if (block.BranchValue is { } branch) last = Evaluate(frame, branch).Read();", "Operand stale = Operand.Value(last);\n                foreach (StageBPrefixNode operation in block.Operations) { stale = Evaluate(frame, operation); last = stale.Read(); }\n                if (block.BranchValue is { } branch) { _ = Evaluate(frame, branch); last = stale.Read(); }", "Control edge adapter changed")]
    [TestCase("foreach (StageBPrefixNode operation in block.Operations) last = Evaluate(frame, operation).Read();\n                if (block.BranchValue is { } branch) last = Evaluate(frame, branch).Read();", "if (block.BranchValue is { } branch) last = Evaluate(frame, branch).Read();\n                foreach (StageBPrefixNode operation in block.Operations) last = Evaluate(frame, operation).Read();", "Control edge adapter changed")]
    [TestCase("if (result.Kind == StageBEdgeKind.Return) return result.Cursor.Carried;", "if (result.Kind == StageBEdgeKind.Return) return StageBValue.Unit;", "Control edge adapter changed")]
    [TestCase("if (block.Exit == StageBPrefixExit.OutsideSelectedDomain) throw new OutsideSignal();", "", "Control edge adapter changed")]
    [TestCase("last.Kind == StageBValueKind.Bool, last.Boolean", "last.Kind == StageBValueKind.Bool, !last.Boolean", "Control edge adapter changed")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            _fuel = long.MaxValue;\n            return node.Kind", "Control fuel write bypass")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            (_fuel)++;\n            return node.Kind", "Control fuel mutation bypass")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            ++(_fuel);\n            return node.Kind", "Control fuel mutation bypass")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            (_fuel)--;\n            return node.Kind", "Control fuel mutation bypass")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            --(_fuel);\n            return node.Kind", "Control fuel mutation bypass")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            (((_fuel)))++;\n            return node.Kind", "Control fuel mutation bypass")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            _ = checked(++(_fuel));\n            return node.Kind", "Control fuel mutation bypass")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            unchecked { ((_fuel))--; }\n            return node.Kind", "Control fuel mutation bypass")]
    [TestCase("private long _fuel;", "private long _fuel; private static void MutateFuel(Machine target) { ((target._fuel))++; }", "Control fuel mutation bypass")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            Tick();\n            return node.Kind", "Control Machine call roster changed")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            GC.KeepAlive(this);\n            return node.Kind", "Control explicit Machine instance escape is unsupported")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            Func<long> capturedFuel = () => _fuel; GC.KeepAlive(capturedFuel);\n            return node.Kind", "Control captured fuel alias is unsupported")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            (_fuel, _) = (long.MaxValue, 0);\n            return node.Kind", "Control fuel write bypass")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            typeof(Machine).GetField(\"_fuel\", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(this, long.MaxValue);\n            return node.Kind", "Control indirect fuel access is unsupported")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            TypedReference alias = __makeref(_fuel); __refvalue(alias, long) = long.MaxValue;\n            return node.Kind", "Control TypedReference intrinsics are unsupported")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            TypedReference alias = default; __refvalue(alias, long) = long.MaxValue;\n            return node.Kind", "Control TypedReference intrinsics are unsupported")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            TypedReference alias = default; _ = __reftype(alias);\n            return node.Kind", "Control TypedReference intrinsics are unsupported")]
    [TestCase("private long _fuel;", "private long _fuel; private static void TypedArgumentSink(__arglist) { } private void TypedArgumentProbe() { TypedArgumentSink(__arglist(_fuel)); }", "Control TypedReference intrinsics are unsupported")]
    [TestCase("Tick();\n            return node.Kind", "Tick();\n            ref long alias = ref _fuel;\n            alias = 7;\n            return node.Kind", "Control fuel alias is unsupported")]
    [TestCase("_fuel = fuel;", "_fuel = 7;", "Control initial fuel adapter changed")]
    [TestCase("private long _fuel;", "private long _storedFuel; private long _fuel { get => _storedFuel; set => _storedFuel = value + 1; }", "Control fuel field changed")]
    [TestCase("private sealed class Machine", "[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)] private sealed class Machine", "Control Machine storage declaration changed")]
    [TestCase("private sealed class Machine", "[System.Serializable] private sealed class Machine", "Control Machine storage declaration changed")]
    [TestCase("private sealed class Machine", "private sealed partial class Machine", "Control Machine storage declaration changed")]
    [TestCase("private sealed class Machine", "private sealed partial class Machine { } private sealed partial class Machine", "Control Machine storage declaration changed")]
    [TestCase("private long _fuel;", "[System.NonSerialized] private long _fuel;", "Control fuel field changed")]
    [TestCase("private readonly List<StageBRequest> _requests = [];", "[System.NonSerialized] private readonly List<StageBRequest> _requests = [];", "Control Machine field attributes are unsupported")]
    [TestCase("fuel <= 0 || fuel > program.FuelBound", "fuel < 0 || fuel > program.FuelBound", "Control initial fuel guard changed")]
    [TestCase("constant[\"string:\".Length..]", "constant[(constant.Length - 1)..]", "Control Machine token shape changed")]
    [TestCase("StageBValue.Enum(type, value.Signed),", "StageBValue.Enum(\"int\", value.Signed),", "Control Machine token shape changed")]
    [TestCase("StageBValue.Enum(\"int\", value.Signed),", "StageBValue.Enum(\"byte\", value.Signed),", "Control Machine token shape changed")]
    [TestCase("if (type == \"int\") return StageBValue.Enum(\"int\", 0);", "if (type == \"int\") return StageBValue.Null(\"int\");", "Control Machine call roster changed")]
    [TestCase("(\"tracer\", \"IsTracingAccess\") => StageBValue.Bool(_input.Tracer.IsTracingAccess)", "(\"tracer\", \"IsTracingAccess\") => StageBValue.Bool(_input.Tracer.IsTracingActions)", "Control Machine call roster changed")]
    [TestCase("(\"tracer\", \"IsTracingAccess\") => StageBValue.Bool(_input.Tracer.IsTracingAccess)", "(\"tracer\", \"IsTracingAccess\") => StageBValue.Bool(!_input.Tracer.IsTracingAccess)", "Control Machine token shape changed")]
    [TestCase("                    (\"EvmExceptionType\", StageBValue.Enum(\"global::Nethermind.Evm.EvmExceptionType\", 0)),\n                    (\"ErrorDescription\", StageBValue.Null(\"string\")));", "                    (\"EvmExceptionType\", StageBValue.Enum(\"global::Nethermind.Evm.EvmExceptionType\", 0)));", "Control Machine call roster changed")]
    [TestCase("StageBValue value = Execute(new Frame(_functions[call.Target.Body], Operand.Value(StageBValue.Unit)));", "StageBValue value = StageBValue.Unit;", "Control Machine")]
    [TestCase("StageBValue value = Default(call.Target.Member.Type);", "StageBValue value = Default(\"bool\");", "Control Machine call roster changed")]
    [TestCase("StageBValue.Enum(type + \".ErrorType\", 0)", "StageBValue.Enum(type + \".ErrorType\", 1)", "Control Machine token shape changed")]
    [TestCase("(\"ErrorDescription\", StageBValue.Null(\"string\"))", "(\"ErrorDescription\", StageBValue.Reference(\"string\", \"\"))", "Control Machine call roster changed")]
    [TestCase("return node.Mode == StageBPrefixOperandMode.Value ? Operand.Value(location.Read()) : location;", "return node.Mode == StageBPrefixOperandMode.Value ? Operand.Value(Default(call.Target.Member.Type)) : location;", "Control Machine call roster changed")]
    [TestCase("StageBValue value = Execute(new Frame(_functions[call.Target.Body], Operand.Value(StageBValue.Unit)));", "StageBValue value = Execute(new Frame(_functions[call.Target.Body], caller.This));", "Control Machine")]
    [TestCase("                    _statics.Add(call.Target.Member.Symbol, cell);", "                    _ = cell;", "Control Machine")]
    [TestCase("return node.Mode == StageBPrefixOperandMode.Value ? Operand.Value(location.Read()) : location;", "return node.Mode == StageBPrefixOperandMode.Value ? Operand.Value(StageBValue.Unit) : location;", "Control Machine")]
    [TestCase("StageBValue value = Execute(new Frame(_functions[call.Target.Body], Operand.Value(StageBValue.Unit)));\n                    cell = new(\"static:\" + call.Target.Member.Symbol, value);\n                    _statics.Add(call.Target.Member.Symbol, cell);", "cell = new(\"static:\" + call.Target.Member.Symbol, StageBValue.Unit);\n                    _statics.Add(call.Target.Member.Symbol, cell);\n                    StageBValue value = Execute(new Frame(_functions[call.Target.Body], Operand.Value(StageBValue.Unit)));\n                    cell.Value = value;", "Control Machine")]
    [TestCase("return returnKind == StageBLocalReturnKind.ReceiverValue ? Operand.Value(receiver.Read()) : receiver;", "return returnKind == StageBLocalReturnKind.ReceiverValue ? Operand.Value(result) : receiver;", "Control local return adapter changed")]
    [TestCase("                cursor = result.Cursor;", "                if (blocks[result.Cursor.Ordinal].Exit == StageBPrefixExit.Return) return result.Cursor.Carried;\n                cursor = result.Cursor;", "Control edge adapter changed")]
    [TestCase("                    _statics.Add(call.Target.Member.Symbol, cell);", "                    _statics[call.Target.Member.Symbol] = cell;", "Control Machine")]
    [TestCase("private long _fuel;", "private long _fuel; private static class StageBEdgeKind { internal const global::Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBEdgeKind InvalidCondition = global::Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBEdgeKind.Jump; internal const global::Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBEdgeKind Return = global::Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBEdgeKind.Return; internal const global::Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBEdgeKind Jump = global::Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBEdgeKind.Jump; internal const global::Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBEdgeKind MissingSuspension = global::Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBEdgeKind.MissingSuspension; }", "Control adapter type rebound")]
    [TestCase("private long _fuel;", "private long _fuel; private readonly record struct StageBTickResult(bool Exhausted, long RemainingFuel) { public static implicit operator StageBTickResult(global::Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBTickResult value) => new(false, 99); }", "Control adapter type rebound")]
    [TestCase("private long _fuel;", "private long _fuel; private readonly record struct StageBFinishResult<T>(global::Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBEdgeKind Kind, StageBBlockCursor<T> Cursor) { public static implicit operator StageBFinishResult<T>(global::Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBFinishResult<T> value) => new(global::Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBEdgeKind.Jump, new(99, value.Cursor.Carried)); }", "Control result property rebound")]
    public void Stage_b_control_rejects_compile_valid_adapter_bypass_or_rebinding(string original, string replacement, string gate)
    {
        CSharpCompilation mutant = Mutate(StageBControlExtractor.InterpreterPath, (original, replacement));
        Assert.That(() => StageBControlExtractor.Generate(mutant), Throws.TypeOf<ExtractionException>().With.Message.Contains(gate));
    }

    [Test]
    public void Stage_b_control_rejects_compile_valid_transparent_helper_rebinding()
    {
        CSharpCompilation mutant = MutateMany(
            (StageBControlExtractor.InterpreterPath, "private long _fuel;", """
                private long _fuel;
                private static class TransparentProxy
                {
                    internal static bool ShouldReadTransparent(bool preservesOperand, bool valueMode) =>
                        StageBControlKernel.ShouldReadTransparent(preservesOperand, valueMode);
                }
                """),
            (StageBControlExtractor.InterpreterPath,
                "StageBControlKernel.ShouldReadTransparent(preservesOperand, node.Mode == StageBPrefixOperandMode.Value)",
                "TransparentProxy.ShouldReadTransparent(preservesOperand, node.Mode == StageBPrefixOperandMode.Value)"));

        Assert.That(() => StageBControlExtractor.Generate(mutant),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("Control transparent adapter changed"));
    }

    [TestCase(
        "StageBOperationKind.FlowCapture or StageBOperationKind.FlowCaptureReference or StageBOperationKind.Discard;",
        "StageBOperationKind.FlowCapture or StageBOperationKind.FlowCaptureReference or StageBOperationKind.Discard or StageBOperationKind.Return;",
        "Control supported operation predicate changed")]
    [TestCase(
        "block.BranchValue is null ? block.Operations : [.. block.Operations, block.BranchValue]",
        "block.Operations",
        "Control validation root traversal changed")]
    [TestCase(
        "foreach (StageBPrefixNode child in node.Children) ValidateNode(child);",
        "_ = node.Children;",
        "Control validation node traversal changed")]
    public void Stage_b_control_rejects_compile_valid_validation_weakening(string original, string replacement, string gate)
    {
        CSharpCompilation mutant = Mutate(StageBControlExtractor.InterpreterPath, (original, replacement));
        Assert.That(() => StageBControlExtractor.Generate(mutant), Throws.TypeOf<ExtractionException>().With.Message.Contains(gate));
    }

    [Test]
    public void Stage_b_control_rejects_compile_valid_static_constructor_return_readmission()
    {
        const string predicate =
            "    private static bool IsSupportedOperation(StageBOperationKind kind) => kind is\n" +
            "        StageBOperationKind.DeclarationExpression or StageBOperationKind.ExpressionStatement or StageBOperationKind.SimpleAssignment or\n" +
            "        StageBOperationKind.Invocation or StageBOperationKind.ObjectCreation or StageBOperationKind.CollectionExpression or\n" +
            "        StageBOperationKind.FieldReference or StageBOperationKind.PropertyReference or StageBOperationKind.LocalReference or\n" +
            "        StageBOperationKind.ParameterReference or StageBOperationKind.InstanceReference or StageBOperationKind.Literal or\n" +
            "        StageBOperationKind.DefaultValue or StageBOperationKind.Binary or StageBOperationKind.Unary or StageBOperationKind.Conversion or\n" +
            "        StageBOperationKind.Parenthesized or StageBOperationKind.Argument or StageBOperationKind.IsPattern or\n" +
            "        StageBOperationKind.ConstantPattern or StageBOperationKind.NegatedPattern or StageBOperationKind.IsNull or\n" +
            "        StageBOperationKind.FlowCapture or StageBOperationKind.FlowCaptureReference or StageBOperationKind.Discard;";
        const string mutable =
            "    private static readonly HashSet<StageBOperationKind> SupportedOperations = [];\n" +
            "    static StageBPrefixInterpreter() => SupportedOperations.Add(StageBOperationKind.Return);\n" +
            "    private static bool IsSupportedOperation(StageBOperationKind kind) => SupportedOperations.Contains(kind);";
        CSharpCompilation mutant = Mutate(StageBControlExtractor.InterpreterPath, (predicate, mutable));

        Assert.That(() => StageBControlExtractor.Generate(mutant),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("Control supported operation predicate changed"));
    }

    [Test]
    public void Stage_b_control_rejects_compile_valid_return_enum_alias()
    {
        CSharpCompilation mutant = MutateMany(
            (StageBControlExtractor.PackagePath + "/StageBPlan.cs",
                "ExpressionStatement, Return, Conditional", "ExpressionStatement, Return = ExpressionStatement, Conditional"),
            (StageBControlExtractor.PackagePath + "/StageBArtifact.cs",
                "StageBOperationKind.Return => \".returnValue\", ", ""));

        Assert.That(() => StageBControlExtractor.Generate(mutant),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("Control operation kind representation changed"));
    }

    [Test]
    public void Stage_b_control_rejects_compile_valid_function_blocks_getter()
    {
        CSharpCompilation mutant = Mutate(StageBControlExtractor.PackagePath + "/StageBPrefixProgram.cs",
            ("long FuelBound, bool MayReturn);", """
                long FuelBound, bool MayReturn)
                {
                    private int _blockReads;
                    private readonly StageBPrefixBlock[] _blocks = Blocks;
                    public StageBPrefixBlock[] Blocks => _blockReads++ == 0 ? _blocks : [];
                }
                """));

        Assert.That(() => StageBControlExtractor.Generate(mutant),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("Control projection record changed: StageBPrefixFunction"));
    }

    [Test]
    public void Stage_b_control_rejects_compile_valid_explicit_layout_fuel_alias()
    {
        const string offset = "[System.Runtime.InteropServices.FieldOffset(";
        CSharpCompilation mutant = Mutate(StageBControlExtractor.InterpreterPath,
            ("private sealed class Machine", "[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)] private sealed class Machine"),
            ("private readonly StageBPrefixProgram _program;", offset + "8)] private readonly StageBPrefixProgram _program;"),
            ("private readonly StageBPostNonceInput _input;", offset + "16)] private readonly StageBPostNonceInput _input;"),
            ("private readonly StageBResponseTape _responses;", offset + "24)] private readonly StageBResponseTape _responses;"),
            ("private readonly Dictionary<string, StageBPrefixFunction> _functions;", offset + "32)] private readonly Dictionary<string, StageBPrefixFunction> _functions;"),
            ("private readonly Dictionary<string, Cell> _statics = new(StringComparer.Ordinal);", offset + "40)] private readonly Dictionary<string, Cell> _statics = new(StringComparer.Ordinal);"),
            ("private readonly List<StageBRequest> _requests = [];", offset + "48)] private readonly List<StageBRequest> _requests = [];"),
            ("private long _fuel;", offset + "0)] private long _fuel; " + offset + "0)] private long _aliasFuel;"),
            ("Tick();\n            return node.Kind", "Tick();\n            _aliasFuel = long.MaxValue; GC.KeepAlive(_aliasFuel);\n            return node.Kind"));
        Assert.That(() => StageBControlExtractor.Generate(mutant),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("Control Machine storage declaration changed"));
    }

    [Test]
    public void Stage_b_control_rejects_compile_valid_enclosing_reflection_helper()
    {
        CSharpCompilation mutant = Mutate(StageBControlExtractor.InterpreterPath,
            ("internal static class StageBPrefixInterpreter\n{", "internal static class StageBPrefixInterpreter\n{\n    private static void ResetFuel(object machine) => machine.GetType().GetField(\"_fuel\", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(machine, long.MaxValue);"),
            ("Tick();\n            return node.Kind", "Tick();\n            ResetFuel(this);\n            return node.Kind"));
        Assert.That(() => StageBControlExtractor.Generate(mutant),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("Control Machine call roster changed"));
    }

    [TestCase("private static void SourceProbe() { }", "SourceProbe();")]
    [TestCase("private static int SourceProbe => 0;", "_ = SourceProbe;")]
    [TestCase("private sealed class SourceProbe { }", "_ = new SourceProbe();")]
    [TestCase("private static void SourceProbe() { }", "Action probe = SourceProbe; GC.KeepAlive(probe);")]
    public void Stage_b_control_rejects_compile_valid_new_source_call_target(string declaration, string statement)
    {
        CSharpCompilation mutant = Mutate(StageBControlExtractor.InterpreterPath,
            ("internal static class StageBPrefixInterpreter\n{", "internal static class StageBPrefixInterpreter\n{\n    " + declaration),
            ("Tick();\n            return node.Kind", "Tick();\n            " + statement + "\n            return node.Kind"));
        Assert.That(() => StageBControlExtractor.Generate(mutant),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("Control Machine call roster changed"));
    }

    [TestCase("_ = System.IO.File.Exists(Probe);")]
    [TestCase("string converted = Probe; GC.KeepAlive(converted);")]
    [TestCase("string converted = string.Empty; converted = Probe; GC.KeepAlive(converted);")]
    [TestCase("_ = System.IO.File.Exists(_fuel > 0 ? Probe : string.Empty);")]
    [TestCase("string[] converted = [Probe]; GC.KeepAlive(converted);")]
    [TestCase("System.Text.StringBuilder converted = new(Probe); GC.KeepAlive(converted);")]
    [TestCase("Console.Title = Probe;")]
    [TestCase("(string converted, int marker) = (Probe, 0); GC.KeepAlive(converted);")]
    [TestCase("Func<string> converted = () => Probe; GC.KeepAlive(converted);")]
    [TestCase("string converted = Probe ?? string.Empty; GC.KeepAlive(converted);")]
    public void Stage_b_control_rejects_compile_valid_implicit_source_conversion(string statement)
    {
        const string declarations = """
            private static object? Escaped;
            private static readonly ProbeType Probe = new();
            private sealed class ProbeType
            {
                public static implicit operator string(ProbeType value)
                {
                    Escaped!.GetType().GetField("_fuel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(Escaped, long.MaxValue);
                    return "";
                }
            }
            """;
        CSharpCompilation mutant = Mutate(StageBControlExtractor.InterpreterPath,
            ("internal static class StageBPrefixInterpreter\n{", "internal static class StageBPrefixInterpreter\n{\n" + declarations),
            ("Tick();\n            return node.Kind", "Tick();\n            Func<object> self = MemberwiseClone; Escaped = self.Target;\n            " + statement + "\n            return node.Kind"));
        Assert.That(() => StageBControlExtractor.Generate(mutant),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("Control Machine call roster changed"));
    }

    [TestCase("Func<object> self = MemberwiseClone; GC.KeepAlive(self.Target);")]
    [TestCase("using System.IO.MemoryStream stream = new();")]
    [TestCase("foreach (int value in new[] { 1 }) GC.KeepAlive(value);")]
    [TestCase("string formatted = $\"{_fuel}\"; GC.KeepAlive(formatted);")]
    [TestCase("AppDomain.CurrentDomain.ProcessExit += static (_, _) => { };")]
    [TestCase("lock (_requests) { }")]
    public void Stage_b_control_rejects_compile_valid_unadmitted_machine_token_changes(string statement)
    {
        CSharpCompilation mutant = Mutate(StageBControlExtractor.InterpreterPath,
            ("Tick();\n            return node.Kind", "Tick();\n            " + statement + "\n            return node.Kind"));
        Assert.That(() => StageBControlExtractor.Generate(mutant),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("Control Machine token shape changed"));
    }

    [Test]
    public void Stage_b_control_machine_token_admission_ignores_trivia()
    {
        CSharpCompilation mutant = Mutate(StageBControlExtractor.InterpreterPath,
            ("private long _fuel;", "private /* unchanged storage */ long _fuel;"));
        Assert.That(StageBControlExtractor.Emit(StageBControlExtractor.Generate(mutant)),
            Is.EqualTo(StageBControlExtractor.Emit(StageBControlExtractor.Generate(_inputs.Compilation))));
    }

    [TestCase("StageBPrefixExecution.cs", "internal bool Boolean { get; }", "internal bool Boolean { get; private set; }", "Control value projection is not an auto getter")]
    [TestCase("StageBPrefixInterpreter.cs", "internal StageBPrefixFunction Function { get; } = function;", "internal StageBPrefixFunction Function => function with { Entry = function.Entry with { Block = function.Entry.Block + 1 } };", "Control frame declaration changed")]
    [TestCase("StageBPrefixProgram.cs", "Branch, Return, Suspend, OutsideSelectedDomain", "Branch, Suspend, Return, OutsideSelectedDomain", "Control projection enum changed")]
    [TestCase("StageBPrefixProgram.cs", "Value, Location, ReadOnlyLocation", "Location, Value, ReadOnlyLocation", "Control projection enum changed")]
    [TestCase("StageBPlan.cs", "internal sealed record StageBEdge(int Destination, string Semantics, int[] LeavingRegions, int[] EnteringRegions, int[] FinallyRegions);", "internal sealed record StageBEdge(int Destination, string Semantics, int[] LeavingRegions, int[] EnteringRegions, int[] FinallyRegions) { public int Destination { get; init; } = Destination + 1; }", "Control projection record changed")]
    [TestCase("StageBPlan.cs", "internal sealed record StageBCfgPoint(int Block, int Operation);", "internal sealed record StageBCfgPoint(int Block, int Operation) { public int Block { get; init; } = Block + 1; }", "Control projection record changed")]
    [TestCase("StageBPrefixProgram.cs", "StageBStage[] AcceptedStages, string Integrity);", "StageBStage[] AcceptedStages, string Integrity) { public long FuelBound { get; init; } = FuelBound + 1; }", "Control projection record changed")]
    [TestCase("StageBPrefixProgram.cs", "int ReceiverChild, StageBPrefixArgument[] Arguments);", "int ReceiverChild, StageBPrefixArgument[] Arguments) { public int ReceiverChild { get; init; } = ReceiverChild + 1; }", "Control projection record changed: StageBPrefixCall")]
    [TestCase("StageBPrefixProgram.cs", "int ReceiverChild, StageBPrefixArgument[] Arguments);", "int ReceiverChild, StageBPrefixArgument[] Arguments) { private StageBPrefixArgument[] _arguments = Arguments; private int _reads; public StageBPrefixArgument[] Arguments { get => _reads++ == 0 ? _arguments : []; init => _arguments = value; } }", "Control projection record changed: StageBPrefixCall")]
    [TestCase("StageBPrefixProgram.cs", "string Body, StageBRequestKind? RequestKind);", "string Body, StageBRequestKind? RequestKind) { public string Body { get; init; } = Body + \"changed\"; }", "Control projection record changed: StageBPrefixTarget")]
    [TestCase("StageBPrefixProgram.cs", "StageBArgumentMode Mode, bool Implicit, string Kind);", "StageBArgumentMode Mode, bool Implicit, string Kind) { public int Child { get; init; } = Child + 1; }", "Control projection record changed: StageBPrefixArgument")]
    [TestCase("StageBPlan.cs", "StageBRefKind RefKind, StageBParameter[] Parameters);", "StageBRefKind RefKind, StageBParameter[] Parameters) { public StageBParameter[] Parameters { get; init; } = Parameters.Reverse().ToArray(); }", "Control projection record changed: StageBMember")]
    [TestCase("StageBPlan.cs", "StageBRefKind RefKind, bool Optional);", "StageBRefKind RefKind, bool Optional) { public StageBRefKind RefKind { get; init; } = StageBRefKind.None; }", "Control projection record changed: StageBParameter")]
    [TestCase("StageBPrefixProgram.cs", "internal sealed record StageBPrefixCall(", "[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] internal sealed record StageBPrefixCall(", "Control projection record changed: StageBPrefixCall")]
    [TestCase("StageBPrefixProgram.cs", "Local, StaticField, DefaultValue, Initialization, StateCharge, ExternalRequest, RefundSuspension", "StaticField, Local, DefaultValue, Initialization, StateCharge, ExternalRequest, RefundSuspension", "Control projection enum changed: StageBPrefixTargetKind")]
    [TestCase("StageBPlan.cs", "None, Ref, Out, In, RefReadOnly, RefReadOnlyParameter", "Ref, None, Out, In, RefReadOnly, RefReadOnlyParameter", "Control projection enum changed: StageBRefKind")]
    [TestCase("StageBPlan.cs", "Value, ReadOnlyLocation, ReadOnlyTemporary, WritableLocation, OutLocation", "ReadOnlyTemporary, ReadOnlyLocation, Value, WritableLocation, OutLocation", "Control projection enum changed: StageBArgumentMode")]
    [TestCase("StageBPlan.cs", "Method, Constructor, Property, Field", "Constructor, Method, Property, Field", "Control projection enum changed: StageBMemberKind")]
    [TestCase("StageBPlan.cs", "Static, Reference, Value", "Reference, Static, Value", "Control projection enum changed: StageBReceiverKind")]
    public void Stage_b_control_rejects_compile_valid_projection_drift(string file, string original, string replacement, string gate)
    {
        CSharpCompilation mutant = Mutate(StageBControlExtractor.PackagePath + "/" + file, (original, replacement));
        Assert.That(() => StageBControlExtractor.Generate(mutant), Throws.TypeOf<ExtractionException>().With.Message.Contains(gate));
    }

    [TestCase("StageBPrefixInterpreter.cs", "internal StageBValue Value { get; set; } = value;", "internal StageBValue Value { get; set; } = StageBValue.Unit;", "Cell")]
    [TestCase("StageBPrefixInterpreter.cs", "internal static Operand Value(StageBValue value) => new(value, null);", "internal static Operand Value(StageBValue value) => new(StageBValue.Unit, null);", "Operand")]
    [TestCase("StageBPrefixInterpreter.cs", "internal static Operand Location(Location target) => new(null, target);", "internal static Operand Location(Location target) => new(target.Read(), null);", "Operand")]
    [TestCase("StageBPrefixInterpreter.cs", "protected bool ReadOnly { get; } = readOnly;", "protected bool ReadOnly { get; } = !readOnly;", "Location")]
    [TestCase("StageBPrefixInterpreter.cs", "internal override StageBValue Read() => Cell.Alias?.Read() ?? Cell.Value;", "internal override StageBValue Read() => Cell.Value;", "CellLocation")]
    [TestCase("StageBPrefixInterpreter.cs", "if (Cell.Alias is { } alias) alias.Write(value);\n            else Cell.Value = value;", "Cell.Value = value;", "CellLocation")]
    [TestCase("StageBPrefixInterpreter.cs", "return resolved with { ReadOnly = ReadOnly || resolved.ReadOnly };", "return resolved with { ReadOnly = ReadOnly };", "CellLocation")]
    [TestCase("StageBPrefixInterpreter.cs", "internal override string Identity => Cell.Alias?.Identity ?? Cell.Identity;", "internal override string Identity => Cell.Identity;", "CellLocation")]
    [TestCase("StageBPrefixInterpreter.cs", "parent.Write(parent.Read().WithField(fieldName, value));", "parent.Write(parent.Read());", "FieldLocation")]
    [TestCase("StageBPrefixInterpreter.cs", "Fields = [.. resolved.Fields, fieldName],", "Fields = resolved.Fields,", "FieldLocation")]
    [TestCase("StageBPrefixInterpreter.cs", "_value = value;", "_value = StageBValue.Unit;", "ValueLocation")]
    [TestCase("StageBPrefixInterpreter.cs", "internal override void Write(StageBValue replacement) => throw new StageBExecutionException(\"readonly\", Identity);", "internal override void Write(StageBValue replacement) { }", "ValueLocation")]
    [TestCase("StageBPrefixInterpreter.cs", "internal override void Write(StageBValue value) { }", "internal override void Write(StageBValue value) => throw new StageBExecutionException(\"discard-write\", type);", "DiscardLocation")]
    [TestCase("StageBPrefixExecution.cs", "internal sealed record StageBResolvedLocation(object Root, string[] Fields, bool ReadOnly, string Provenance);", "internal sealed record StageBResolvedLocation(object Root, string[] Fields, bool ReadOnly, string Provenance) { public bool ReadOnly { get; init; } = false; }", "StageBResolvedLocation")]
    [TestCase("StageBPrefixExecution.cs", "Signed = signed;", "Signed = signed + 1;", "StageBValue")]
    [TestCase("StageBPrefixExecution.cs", "fields[name] = value;", "fields[name] = Field(name);", "StageBValue")]
    public void Stage_b_control_rejects_compile_valid_runtime_representation_drift(string file, string original, string replacement, string type)
    {
        CSharpCompilation mutant = Mutate(StageBControlExtractor.PackagePath + "/" + file, (original, replacement));
        Assert.That(() => StageBControlExtractor.Generate(mutant), Throws.TypeOf<ExtractionException>()
            .With.Message.EqualTo("Control runtime representation changed: " + type));
        Assert.That(ObserveRuntimeRepresentations(mutant), Is.Not.EqualTo(ExpectedRuntimeRepresentations), type);
    }

    [Test]
    public void Stage_b_control_runtime_representation_observations_preserve_aliases_copies_and_readonly_locations()
    {
        Assert.That(() => StageBControlExtractor.Generate(_inputs.Compilation), Throws.Nothing);
        Assert.That(ObserveRuntimeRepresentations(_inputs.Compilation), Is.EqualTo(ExpectedRuntimeRepresentations));
    }

    [Test]
    public void Stage_b_control_rejects_compile_valid_runtime_representation_rebinding()
    {
        CSharpCompilation mutant = Mutate(StageBControlExtractor.PackagePath + "/StageBPrefixExecution.cs",
            ("namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;",
                "namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor;\n" +
                "internal static class StringComparer { internal static System.StringComparer Ordinal => System.StringComparer.OrdinalIgnoreCase; internal static System.StringComparer OrdinalIgnoreCase => System.StringComparer.OrdinalIgnoreCase; }"));
        Assert.That(() => StageBControlExtractor.Generate(mutant), Throws.TypeOf<ExtractionException>()
            .With.Message.EqualTo("Control runtime representation binding changed: StringComparer"));
    }

    private static object[] ExpectedRuntimeRepresentations =>
        [71L, 37L, "source", true, true, "source", 83L, true, 43L, 59L, true, "readonly", 23L, 71L, 11L, 19L,
            true, "value", "record.value", "ok"];

    private static object[] ObserveRuntimeRepresentations(CSharpCompilation compilation)
    {
        const string probe = """
            private static object[] ProbeRuntimeRepresentations()
            {
                static string Write(Operand operand)
                {
                    try { operand.Write(StageBValue.Int64(97)); return "ok"; }
                    catch (StageBExecutionException exception) { return exception.Code; }
                }
                try
                {
                    Cell source = new("source", StageBValue.Int64(71));
                    Cell alias = new("alias", StageBValue.Int64(0)) { Alias = new CellLocation(source, false) };
                    CellLocation location = new(alias, false);
                    long read = location.Read().Signed;
                    location.Write(StageBValue.Int64(37));
                    long written = source.Value.Signed;
                    Cell protectedAlias = new("protected", StageBValue.Int64(0)) { Alias = new CellLocation(source, true) };
                    StageBResolvedLocation resolved = new CellLocation(protectedAlias, false).Resolve();
                    Operand reference = Operand.Location(location);
                    source.Value = StageBValue.Int64(83);
                    Operand copy = Operand.Value(StageBValue.Int64(43));
                    ValueLocation temporary = new("temporary:value", StageBValue.Int64(59));
                    string readonlyWrite = Write(Operand.Location(temporary));
                    Cell record = new("record", StageBValue.Struct("record", ("value", StageBValue.Int64(11)), ("other", StageBValue.Int64(19))));
                    StageBValue original = record.Value;
                    FieldLocation field = new(new CellLocation(record, false), "value", false);
                    field.Write(StageBValue.Int64(23));
                    long fieldWritten = field.Read().Signed;
                    field.Write(StageBValue.Int64(71));
                    StageBResolvedLocation resolvedField = field.Resolve();
                    return [read, written, location.Identity, ReferenceEquals(resolved.Root, source), resolved.ReadOnly, resolved.Provenance,
                        reference.Read().Signed, ReferenceEquals(reference.Target, location), copy.Read().Signed, temporary.Read().Signed, temporary.Resolve().ReadOnly,
                        readonlyWrite, fieldWritten, field.Read().Signed, original.Field("value").Signed, record.Value.Field("other").Signed,
                        ReferenceEquals(resolvedField.Root, record), string.Join('.', resolvedField.Fields), resolvedField.Provenance,
                        Write(Operand.Location(new DiscardLocation("long")))];
                }
                catch (StageBExecutionException exception) { return [exception.Code]; }
            }
            """;
        SyntaxTree tree = compilation.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("StageBPrefixInterpreter.cs", StringComparison.Ordinal));
        string source = tree.GetText().ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        source = source.Replace("internal static class StageBPrefixInterpreter\n{", "internal static class StageBPrefixInterpreter\n{\n" + probe, StringComparison.Ordinal);
        CSharpCompilation candidate = compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(source, (CSharpParseOptions)tree.Options, tree.FilePath));
        using MemoryStream assembly = new();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = candidate.Emit(assembly);
        Assert.That(emitted.Success, Is.True, string.Join('\n', emitted.Diagnostics));
        assembly.Position = 0;
        System.Runtime.Loader.AssemblyLoadContext context = new(null, isCollectible: true);
        try
        {
            System.Reflection.Assembly loaded = context.LoadFromStream(assembly);
            System.Reflection.MethodInfo method = loaded.GetType("Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.StageBPrefixInterpreter")!
                .GetMethod("ProbeRuntimeRepresentations", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            return (object[])method.Invoke(null, null)!;
        }
        finally { context.Unload(); }
    }

    [Test]
    public async Task Stage_b_control_baseline_proof_and_canonical_artifacts_pass()
    {
        string emitted = StageBControlExtractor.Emit(StageBControlExtractor.Generate(_inputs.Compilation));
        (int exit, string output) = await CheckProof(emitted);
        Assert.That(exit, Is.Zero, output);
        string generated = Path.Combine(_root, StageBControlExtractor.PackagePath, "StageB/Control/Generated");
        Assert.That(File.ReadAllText(Path.Combine(generated, StageBControlExtractor.LeanName)), Is.EqualTo(emitted));
        string ir = JsonSerializer.Serialize(StageBControlExtractor.Generate(_inputs.Compilation), CompilerReferences.JsonOptions) + "\n";
        Assert.That(File.ReadAllText(Path.Combine(generated, StageBControlExtractor.IrName)), Is.EqualTo(ir));
    }

    [TestCase("ir-unknown")]
    [TestCase("ir-duplicate")]
    [TestCase("ir-version")]
    [TestCase("manifest-unknown")]
    [TestCase("manifest-duplicate")]
    [TestCase("manifest-source")]
    [TestCase("lean")]
    [TestCase("missing")]
    [TestCase("extra")]
    public void Stage_b_control_artifact_admission_is_exact_and_fail_closed(string mutation)
    {
        Dictionary<string, byte[]> fresh = StageBControlExtractor.RenderArtifacts(_inputs);
        Dictionary<string, byte[]> changed = new(fresh, StringComparer.Ordinal);
        string file = mutation.StartsWith("manifest", StringComparison.Ordinal) ? StageBControlExtractor.ManifestName : StageBControlExtractor.IrName;
        string text = Encoding.UTF8.GetString(changed[file]);
        switch (mutation)
        {
            case "ir-unknown": case "manifest-unknown": text = "{\"extra\":false," + text[1..]; break;
            case "ir-duplicate": case "manifest-duplicate": text = "{\"schemaVersion\":2," + text[1..]; break;
            case "ir-version": text = text.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 3", StringComparison.Ordinal); break;
            case "manifest-source": text = text.Replace("StageBControlKernel.cs", "DifferentKernel.cs", StringComparison.Ordinal); break;
            case "lean": changed[StageBControlExtractor.LeanName] = Encoding.UTF8.GetBytes("namespace Wrong\nend Wrong\n"); break;
            case "missing": changed.Remove(StageBControlExtractor.LeanName); break;
            case "extra": changed.Add("extra", []); break;
            default: throw new AssertionException(mutation);
        }
        changed[file] = Encoding.UTF8.GetBytes(text);
        Assert.That(() => StageBControlExtractor.ValidateArtifacts(fresh, StageBControlExtractor.RenderArtifacts(_inputs)), Throws.Nothing);
        Assert.That(() => StageBControlExtractor.ValidateArtifacts(changed, fresh), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Stage_b_control_directory_check_rejects_an_actual_extra_file()
    {
        Dictionary<string, byte[]> fresh = StageBControlExtractor.RenderArtifacts(_inputs);
        string path = Path.Combine(_root, StageBControlExtractor.PackagePath, ".lake", "control-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            foreach ((string name, byte[] bytes) in fresh) File.WriteAllBytes(Path.Combine(path, name), bytes);
            Assert.That(() => StageBControlExtractor.ValidateDirectory(path, fresh), Throws.Nothing);
            File.WriteAllText(Path.Combine(path, "extra.json"), "{}");
            Assert.That(() => StageBControlExtractor.ValidateDirectory(path, fresh), Throws.TypeOf<ExtractionException>().With.Message.Contains("artifact roster changed"));
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    private CSharpCompilation Mutate(string relative, params (string Original, string Replacement)[] replacements)
    {
        SyntaxTree tree = _inputs.Compilation.SyntaxTrees.Single(tree => Path.GetFullPath(tree.FilePath) == Path.GetFullPath(Path.Combine(_root, relative)));
        string source = tree.GetText().ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach ((string original, string replacement) in replacements)
        {
            Assert.That(source.Split(original, StringSplitOptions.None), Has.Length.EqualTo(2), "Mutation target must be unique.");
            source = source.Replace(original, replacement, StringComparison.Ordinal);
        }
        SyntaxTree changed = CSharpSyntaxTree.ParseText(source, (CSharpParseOptions)tree.Options, tree.FilePath);
        CSharpCompilation mutant = _inputs.Compilation.ReplaceSyntaxTree(tree, changed);
        Assert.That(() => StageBControlExtractor.RequireCompiles(mutant), Throws.Nothing, "The negative must reach admission after compiling.");
        return mutant;
    }

    private CSharpCompilation MutateMany(params (string Relative, string Original, string Replacement)[] replacements)
    {
        CSharpCompilation mutant = _inputs.Compilation;
        foreach ((string relative, string original, string replacement) in replacements)
        {
            SyntaxTree tree = mutant.SyntaxTrees.Single(tree => Path.GetFullPath(tree.FilePath) == Path.GetFullPath(Path.Combine(_root, relative)));
            string source = tree.GetText().ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.That(source.Split(original, StringSplitOptions.None), Has.Length.EqualTo(2), "Mutation target must be unique.");
            SyntaxTree changed = CSharpSyntaxTree.ParseText(source.Replace(original, replacement, StringComparison.Ordinal),
                (CSharpParseOptions)tree.Options, tree.FilePath);
            mutant = mutant.ReplaceSyntaxTree(tree, changed);
        }
        Assert.That(() => StageBControlExtractor.RequireCompiles(mutant), Throws.Nothing, "The negative must reach admission after compiling.");
        return mutant;
    }

    private async Task<(int Exit, string Output)> CheckProof(string emitted)
    {
        string package = Path.Combine(_root, StageBControlExtractor.PackagePath);
        string path = Path.Combine(package, ".lake", "control-proof-" + Guid.NewGuid().ToString("N") + ".lean");
        string proof = File.ReadAllText(Path.Combine(package, "StageB/Control/Refinement.lean"));
        proof = string.Join('\n', proof.Split('\n').Where(static line => !line.StartsWith("import ", StringComparison.Ordinal)));
        await File.WriteAllTextAsync(path, "import SimpleTransferCompletionExtractor.StageB.Control.Specification\n" + emitted + proof);
        try
        {
            ProcessStartInfo start = new("lake")
            {
                WorkingDirectory = package, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string argument in new[] { "env", "lean", "-DwarningAsError=true", path }) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ?? throw new AssertionException("Lean did not start.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await output + await error);
        }
        finally { File.Delete(path); }
    }
}
