// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;
using YamlDotNet.RepresentationModel;

namespace Ethereum.Beacon.Test;

/// <summary>
/// Consensus-spec <c>fulu/operations</c> tests: one parameterized test per operation handler,
/// each exercising the corresponding <see cref="BlockProcessing"/> sub-transition.
/// </summary>
[TestFixture]
public class OperationsTests
{
    /// <summary>
    /// The test vectors are generated without a scheduled BPO fork, so Fulu's
    /// <c>get_blob_parameters</c> falls back to the Electra blob limit.
    /// </summary>
    private const ulong MaxBlobsPerBlock = 9;

    [TestCaseSource(nameof(Cases), new object[] { "attestation" })]
    public void Attestation(string casePath) =>
        RunOperation<Attestation>(casePath, "attestation.ssz_snappy", static (state, attestation, context) =>
            BlockProcessing.ProcessAttestation(state, attestation, context.EpochCache, context.Pubkeys, context.VerifySignatures));

    [TestCaseSource(nameof(Cases), new object[] { "attester_slashing" })]
    public void Attester_slashing(string casePath) =>
        RunOperation<AttesterSlashing>(casePath, "attester_slashing.ssz_snappy", static (state, slashing, context) =>
            BlockProcessing.ProcessAttesterSlashing(state, slashing, context.EpochCache, context.Pubkeys, context.VerifySignatures));

    [TestCaseSource(nameof(Cases), new object[] { "block_header" })]
    public void Block_header(string casePath) =>
        RunOperation<BeaconBlock>(casePath, "block.ssz_snappy", static (state, block, _) =>
            BlockProcessing.ProcessBlockHeader(state, block));

    [TestCaseSource(nameof(Cases), new object[] { "bls_to_execution_change" })]
    public void Bls_to_execution_change(string casePath) =>
        RunOperation<SignedBlsToExecutionChange>(casePath, "address_change.ssz_snappy", static (state, change, context) =>
            BlockProcessing.ProcessBlsToExecutionChange(state, change, context.VerifySignatures));

    [TestCaseSource(nameof(Cases), new object[] { "consolidation_request" })]
    public void Consolidation_request(string casePath) =>
        RunOperation<ConsolidationRequest>(casePath, "consolidation_request.ssz_snappy", static (state, request, context) =>
            BlockProcessing.ProcessConsolidationRequest(state, request, context.EpochCache));

    [TestCaseSource(nameof(Cases), new object[] { "deposit" })]
    public void Deposit(string casePath) =>
        RunOperation<Deposit>(casePath, "deposit.ssz_snappy", static (state, deposit, _) =>
            BlockProcessing.ProcessDeposit(state, deposit));

    [TestCaseSource(nameof(Cases), new object[] { "deposit_request" })]
    public void Deposit_request(string casePath) =>
        RunOperation<DepositRequest>(casePath, "deposit_request.ssz_snappy", static (state, request, _) =>
            BlockProcessing.ProcessDepositRequest(state, request));

    [TestCaseSource(nameof(Cases), new object[] { "execution_payload" })]
    public void Execution_payload(string casePath)
    {
        bool executionValid = ReadExecutionValid(casePath);
        RunOperation<BeaconBlockBody>(casePath, "body.ssz_snappy", (state, body, _) =>
            BlockProcessing.ProcessExecutionPayload(state, body, new StubPayloadNotifier(executionValid), MaxBlobsPerBlock));
    }

    [TestCaseSource(nameof(Cases), new object[] { "proposer_slashing" })]
    public void Proposer_slashing(string casePath) =>
        RunOperation<ProposerSlashing>(casePath, "proposer_slashing.ssz_snappy", static (state, slashing, context) =>
            BlockProcessing.ProcessProposerSlashing(state, slashing, context.EpochCache, context.Pubkeys, context.VerifySignatures));

    [TestCaseSource(nameof(Cases), new object[] { "sync_aggregate" })]
    public void Sync_aggregate(string casePath) =>
        RunOperation<SyncAggregate>(casePath, "sync_aggregate.ssz_snappy", static (state, syncAggregate, context) =>
            BlockProcessing.ProcessSyncAggregate(state, syncAggregate, context.EpochCache, context.VerifySignatures));

    [TestCaseSource(nameof(Cases), new object[] { "voluntary_exit" })]
    public void Voluntary_exit(string casePath) =>
        RunOperation<SignedVoluntaryExit>(casePath, "voluntary_exit.ssz_snappy", static (state, exit, context) =>
            BlockProcessing.ProcessVoluntaryExit(state, exit, context.EpochCache, context.Pubkeys, context.VerifySignatures));

    [TestCaseSource(nameof(Cases), new object[] { "withdrawal_request" })]
    public void Withdrawal_request(string casePath) =>
        RunOperation<WithdrawalRequest>(casePath, "withdrawal_request.ssz_snappy", static (state, request, context) =>
            BlockProcessing.ProcessWithdrawalRequest(state, request, context.EpochCache));

    [TestCaseSource(nameof(Cases), new object[] { "withdrawals" })]
    public void Withdrawals(string casePath) =>
        RunOperation<ExecutionPayload>(casePath, "execution_payload.ssz_snappy", static (state, payload, _) =>
            BlockProcessing.ProcessWithdrawals(state, payload));

    private static IEnumerable<TestCaseData> Cases(string handler) =>
        BeaconStateTestRunner.EnumerateCases("fulu", "operations", handler);

    private delegate void OperationTransition<in T>(BeaconStateFulu state, T operation, OperationContext context);

    /// <summary>
    /// Decodes the case's operation, then applies the standard pre/post state assertion.
    /// <c>bls_setting: 2</c> in <c>meta.yaml</c> turns signature verification off.
    /// </summary>
    private static void RunOperation<T>(string casePath, string fileName, OperationTransition<T> transition) where T : ISszCodec<T>
    {
        T.Decode(BeaconConsensusTestLoader.ReadSszSnappy(Path.Combine(casePath, fileName)), out T operation);
        bool verifySignatures = !(BeaconStateTestRunner.ReadMeta(casePath).TryGetValue("bls_setting", out string? blsSetting) && blsSetting == "2");
        BeaconStateTestRunner.RunStateTest(casePath, state => transition(state, operation, new OperationContext(state, verifySignatures)));
    }

    private static bool ReadExecutionValid(string casePath)
    {
        using StreamReader reader = new(Path.Combine(casePath, "execution.yaml"));
        YamlStream yaml = [];
        yaml.Load(reader);
        YamlMappingNode root = (YamlMappingNode)yaml.Documents[0].RootNode;
        return ((YamlScalarNode)root[new YamlScalarNode("execution_valid")]).Value == "true";
    }

    /// <summary>Per-case caches; the pubkey cache is built lazily since only the signed operations need it.</summary>
    private sealed class OperationContext(BeaconStateFulu state, bool verifySignatures)
    {
        private PubkeyCache? _pubkeys;

        public EpochCache EpochCache { get; } = new();

        public bool VerifySignatures { get; } = verifySignatures;

        public PubkeyCache Pubkeys => _pubkeys ??= BuildPubkeys();

        private PubkeyCache BuildPubkeys()
        {
            PubkeyCache pubkeys = new();
            pubkeys.Build(state.Validators!);
            return pubkeys;
        }
    }

    private sealed class StubPayloadNotifier(bool valid) : INewPayloadNotifier
    {
        public bool NotifyNewPayload(BeaconBlockBody body) => valid;
    }
}
