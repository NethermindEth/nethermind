// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Collections;
using Ethereum.Ssz.Test;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using static Ethereum.ConsensusSpec.Test.OperationVectorHandlers;

namespace Ethereum.ConsensusSpec.Test;

[TestFixture]
public class OperationsTests
{
    [Test]
    public void Withdrawal_batch_enforces_capacity_sequentially_and_still_processes_full_exits([Values] bool gloas, [Values(0, 1)] int remaining)
    {
#if !MINIMAL_PRESET
        Assert.Ignore("The minimal queue bound exercises capacity without allocating a mainnet-sized queue.");
#endif
        ulong slot = (Presets.ShardCommitteePeriod + 1) * Presets.SlotsPerEpoch + 1;
        byte[] credentials = new byte[32];
        credentials[0] = Presets.CompoundingWithdrawalPrefix;
        Validator[] validators = [.. Enumerable.Range(0, 4).Select(i => new Validator
        {
            Pubkey = new BlsPublicKey(Enumerable.Repeat((byte)(i + 1), BlsPublicKey.Length).ToArray()),
            WithdrawalCredentials = new Hash256(credentials), EffectiveBalance = Presets.MinActivationBalance,
            ActivationEpoch = 0, ExitEpoch = Presets.FarFutureEpoch, WithdrawableEpoch = Presets.FarFutureEpoch,
        })];
        ulong[] balances = [.. Enumerable.Repeat(3 * Presets.MinActivationBalance, validators.Length)];
        PendingPartialWithdrawal[] pending = [.. Enumerable.Range(0, Presets.PendingPartialWithdrawalsLimit - remaining)
            .Select(_ => new PendingPartialWithdrawal { ValidatorIndex = 0, Amount = 1, WithdrawableEpoch = Presets.FarFutureEpoch })];
        Hash256[] roots = [.. Enumerable.Repeat(Hash256.Zero, (int)Presets.SlotsPerHistoricalRoot)];
        WithdrawalRequest Request(int index, ulong amount) => new() { ValidatorPubkey = validators[index].Pubkey, SourceAddress = Address.Zero, Amount = amount };
        WithdrawalRequest[] requests = [Request(1, 1), Request(2, 1), Request(3, 0)];
        PendingPartialWithdrawal[] actual;
        if (gloas)
        {
            ExecutionRequestsGloas executionRequests = new() { Withdrawals = requests };
            BeaconStateGloas state = new()
            {
                Slot = slot,
                GenesisValidatorsRoot = Hash256.Zero,
                Validators = validators,
                Balances = balances,
                BlockRoots = roots,
                PendingPartialWithdrawals = pending,
                PendingDeposits = [],
                PendingConsolidations = [],
                LatestBlockHeader = new() { Slot = slot - 1 },
                LatestExecutionPayloadBid = new() { BlockHash = Hash256.Zero, ExecutionRequestsRoot = SszRoots.HashTreeRoot(executionRequests) },
                BuilderPendingPayments = [.. Enumerable.Range(0, (int)Presets.BuilderPendingPaymentsLength).Select(_ => new BuilderPendingPayment { Withdrawal = new() })],
                ExecutionPayloadAvailability = new BitArray((int)Presets.SlotsPerHistoricalRoot),
            };
            BeaconBlockGloas block = new() { Body = new() { SignedExecutionPayloadBid = new() { Message = new() { ParentBlockHash = Hash256.Zero } }, ParentExecutionRequests = executionRequests } };
            GloasBlockProcessing.ProcessParentExecutionPayload(state, block, new EpochCache());
            actual = state.PendingPartialWithdrawals!;
        }
        else
        {
            BeaconStateFulu state = new()
            {
                Slot = slot,
                GenesisValidatorsRoot = Hash256.Zero,
                Validators = validators,
                Balances = balances,
                BlockRoots = roots,
                PendingPartialWithdrawals = pending,
                PendingDeposits = [],
                PendingConsolidations = [],
                Eth1Data = new(),
            };
            BlockProcessing.ProcessOperations(state, new() { ExecutionRequests = new() { Withdrawals = requests } }, new EpochCache(), new PubkeyCache(), verifySignatures: false);
            actual = state.PendingPartialWithdrawals!;
        }
        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(actual.Length, Is.EqualTo(Presets.PendingPartialWithdrawalsLimit));
        Assert.That(actual.Skip(pending.Length).Select(w => w.ValidatorIndex), Is.EqualTo(remaining == 0 ? Array.Empty<ulong>() : new ulong[] { 1 }));
        Assert.That(validators[3].ExitEpoch, Is.Not.EqualTo(Presets.FarFutureEpoch));
    }

    [TestCaseSource(nameof(MinimalCases))]
    public void Vector(OperationCase testCase) => Execute(testCase);
    [TestCaseSource(nameof(MainnetCases))]
    public void Vector_mainnet(OperationCase testCase) => Execute(testCase);

    [TestCase("fulu", "bls_to_execution_change", "success")]
    [TestCase("gloas", "bls_to_execution_change", "success")]
    [TestCase("fulu", "voluntary_exit", "basic")]
    [TestCase("gloas", "voluntary_exit", "basic")]
    [TestCase("fulu", "proposer_slashing", "basic")]
    [TestCase("gloas", "proposer_slashing", "basic")]
    [TestCase("gloas", "withdrawals", "early_return_empty_parent_block")]
    public void Required_successful_operation_contract(string fork, string operation, string name)
    {
        string path = Path.Combine(ConsensusSpecArchive.GetRoot(FuluDriverSupport.CompiledPreset),
            "tests", ConsensusSpecArchive.PresetDirName(FuluDriverSupport.CompiledPreset), fork, "operations", operation, "pyspec_tests", name);
        foreach (string file in new[] { "pre.ssz_snappy", "post.ssz_snappy" })
            Assert.That(File.Exists(Path.Combine(path, file)), Is.True, $"mandatory positive vector is missing {file}");
        Assert.That(() => Execute(new OperationCase(FuluDriverSupport.CompiledPreset.ToString(), fork, operation, path,
            $"{FuluDriverSupport.CompiledPreset}/{fork}/operations/{operation}/pyspec_tests/{name}")), Throws.Nothing);
    }

    [TestCase("attester_slashing", "invalid_same_data", "not slashable")]
    [TestCase("attester_slashing", "invalid_incorrect_sig_2", "attestation 2 is invalid")]
    [TestCase("proposer_slashing", "invalid_headers_are_same_sigs_are_same", "identical")]
    [TestCase("proposer_slashing", "invalid_slots_same_epoch_different_slot", "header slots do not match")]
    [TestCase("proposer_slashing", "invalid_different_proposer_indices", "proposer indices do not match")]
    [TestCase("proposer_slashing", "invalid_incorrect_proposer_index", "is out of range")]
    [TestCase("proposer_slashing", "invalid_incorrect_sig_2", "Invalid proposer slashing signature")]
    [TestCase("proposer_slashing", "invalid_proposer_is_slashed", "not slashable")]
    [TestCase("voluntary_exit", "invalid_incorrect_signature", "Invalid voluntary exit signature")]
    [TestCase("voluntary_exit", "invalid_validator_has_pending_withdrawal", "pending partial withdrawals")]
    [TestCase("voluntary_exit", "invalid_validator_already_exited", "already initiated an exit")]
    [TestCase("voluntary_exit", "invalid_validator_not_active", "is not active")]
    [TestCase("voluntary_exit", "invalid_validator_incorrect_validator_index", "is out of range")]
    [TestCase("voluntary_exit", "invalid_validator_not_active_long_enough", "not been active long enough", Presets.DefaultSlotsPerEpoch)]
    [TestCase("voluntary_exit", "invalid_validator_exit_in_future", "not valid before epoch")]
    [TestCase("bls_to_execution_change", "invalid_bad_signature", "Invalid BLS to execution change signature")]
    [TestCase("bls_to_execution_change", "invalid_incorrect_from_bls_pubkey", "does not match the withdrawal credentials")]
    [TestCase("bls_to_execution_change", "invalid_already_0x01", "does not have BLS withdrawal credentials")]
    [TestCase("bls_to_execution_change", "invalid_val_index_out_of_range", "is out of range")]
    [TestCase("execution_payload_bid", "process_execution_payload_bid_insufficient_balance", "cannot cover a bid")]
    [TestCase("withdrawals", "invalid_builder_index_pending", "out of range")]
    [TestCase("withdrawals", "invalid_builder_index_sweep", "out of range")]
    [TestCase("withdrawals", "invalid_validator_index_pending_partial", "out of range")]
    [TestCase("withdrawals", "invalid_validator_index_sweep", "out of range")]
    public void Required_invalid_gloas_operation_preserves_the_state(string operation, string name, string expectedMessage, ulong? slot = null)
    {
        string path = Path.Combine(ConsensusSpecArchive.GetRoot(FuluDriverSupport.CompiledPreset),
            "tests", ConsensusSpecArchive.PresetDirName(FuluDriverSupport.CompiledPreset), "gloas", "operations", operation, "pyspec_tests", name);
        (string? file, Action<OpContext<BeaconStateGloas>, byte[], BlockSignatureBatch?> apply) = GloasHandlers[operation];
        foreach (string required in new[] { "pre.ssz_snappy", file }.OfType<string>())
            Assert.That(File.Exists(Path.Combine(path, required)), Is.True, $"mandatory negative vector is missing {required}");
        Assert.That(File.Exists(Path.Combine(path, "post.ssz_snappy")), Is.False, "the required vector must describe a rejection");
        BeaconStateGloas.Decode(SszConsensusTestLoader.ReadSszSnappy(Path.Combine(path, "pre.ssz_snappy")), out BeaconStateGloas state);
        if (slot is { } targetSlot) state.Slot = targetSlot;
        OpContext<BeaconStateGloas> context = new(state, new EpochCache(), FuluDriverSupport.BuildPubkeyCache(state.Validators!),
            true, true, FuluDriverSupport.CaseSpec(path), path);
        byte[] operand = file is null ? [] : SszConsensusTestLoader.ReadSszSnappy(Path.Combine(path, file));
        Nethermind.Core.Crypto.Hash256 rootBefore = SszRoots.HashTreeRoot(state);

        BeaconStateException exception = Assert.Throws<BeaconStateException>(() => apply(context, operand, null))!;

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(exception.Message, Does.Contain(expectedMessage));
        Assert.That(SszRoots.HashTreeRoot(state), Is.EqualTo(rootBefore), "a rejected operation must leave the state untouched");
    }

    private static readonly Dictionary<string, string[]> OperationsAbsentByFork = new(StringComparer.Ordinal)
    {
        ["fulu"] = ["deposit"],
    };
    [Test]
    public void Every_fork_and_handler_has_vectors_in_the_archive([Values] ConsensusPreset preset)
    {
        List<OperationCase> cases = FuluDriverSupport.TestedCases<OperationCase>(preset, MinimalCases, MainnetCases);
        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(cases.Select(static testCase => testCase.Fork).Distinct(), Is.EquivalentTo(ConsensusSpecArchive.StateTransitionForks));
        foreach (string fork in ConsensusSpecArchive.StateTransitionForks)
        {
            IEnumerable<string> table = FuluDriverSupport.RequireForkDriver(fork) is ForkDriver<BeaconStateGloas> ? GloasHandlers.Keys : Handlers.Keys;
            Assert.That(
                cases.Where(testCase => testCase.Fork == fork).Select(static testCase => testCase.OperationName).Distinct(),
                Is.EquivalentTo(table.Except(OperationsAbsentByFork.GetValueOrDefault(fork, []))),
                $"{fork} operations");
        }
    }
    [Test]
    public void Every_fork_and_handler_runs_a_mainnet_vector_rather_than_reporting_it_not_implemented() =>
        FuluDriverSupport.AssertEveryKeyRunsAVector(
            FuluDriverSupport.TestedCases<OperationCase>(FuluDriverSupport.CompiledPreset, MinimalCases, MainnetCases),
            static testCase => $"{testCase.Fork}/{testCase.OperationName}",
            Run);

    private static void Execute(OperationCase testCase) =>
        ConsensusSpecTestSummary.RunAndRecord("operations", testCase.Fork, testCase.Preset, testCase.VectorName, () => Run(testCase));

    private static void Run(OperationCase testCase)
    {
        FuluDriverSupport.RequireCompiledPreset(testCase.Preset);

        FuluDriverSupport.Dispatch(testCase.Fork, testCase,
            static (testCase, driver) => Run(testCase, driver, Handlers),
            static (testCase, driver) => Run(testCase, driver, GloasHandlers), "operations handler table");
    }

    private static void Run<TState>(OperationCase testCase, ForkDriver<TState> driver, Dictionary<string, (string? File, Action<OpContext<TState>, byte[], BlockSignatureBatch?> Apply)> handlers)
        where TState : class
    {
        if (!handlers.TryGetValue(testCase.OperationName, out (string? File, Action<OpContext<TState>, byte[], BlockSignatureBatch?> Apply) handler))
            throw new NotImplementedInDriverException($"operation '{testCase.OperationName}' has no handler in this driver.");

        string? operandPath = handler.File is null ? null : Path.Combine(testCase.CasePath, handler.File);
        if (operandPath is not null && !File.Exists(operandPath))
            throw new NotImplementedInDriverException($"expected operand file '{handler.File}' is missing for this vector.");

        TState state = driver.DecodePre(Path.Combine(testCase.CasePath, "pre.ssz_snappy"));
        EpochCache cache = driver.NewCache();
        // Primes the incremental hasher so the post-state root is taken as a diff against the pre-state, as in production.
        driver.CachedRoot(state, cache);
        OpContext<TState> ctx = new(
            state,
            cache,
            FuluDriverSupport.BuildPubkeyCache(driver.ValidatorsOf(state)),
            FuluDriverSupport.ShouldVerifySignatures(testCase.CasePath),
            FuluDriverSupport.ReadExecutionValid(testCase.CasePath),
            FuluDriverSupport.CaseSpec(testCase.CasePath),
            testCase.CasePath);

        byte[] operand = operandPath is null ? [] : SszConsensusTestLoader.ReadSszSnappy(operandPath);
        string postPath = Path.Combine(testCase.CasePath, "post.ssz_snappy");
        FuluDriverSupport.AssertTransition(driver, postPath, ctx.State, cache,
            () => handler.Apply(ctx, operand, null), "the operation",
            "expected the operation to be accepted, but it threw");
    }

    private static IEnumerable<TestCaseData> MinimalCases() => Cases(ConsensusPreset.Minimal);

    private static IEnumerable<TestCaseData> MainnetCases() =>
        ConsensusSpecArchive.MainnetEnabled ? Cases(ConsensusPreset.Mainnet) : [];

    private static IEnumerable<TestCaseData> Cases(ConsensusPreset preset) =>
        FuluDriverSupport.HandlerCases(preset, ConsensusSpecArchive.StateTransitionForks, "operations", "pre.ssz_snappy",
            static (p, fork, handler, path, name) => new OperationCase(p.ToString(), fork, handler, path, name), strictHandlerDirectory: true);
}

public readonly record struct OperationCase(string Preset, string Fork, string OperationName, string CasePath, string VectorName)
{
    public override string ToString() => VectorName;
}
