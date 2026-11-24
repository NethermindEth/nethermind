// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Body = Nethermind.BeaconChain.Legacy.Deneb.BeaconBlockBody;

namespace Nethermind.BeaconChain.Legacy.Capella;

/// <summary>The Capella state transition: withdrawals, credential changes and historical summaries.</summary>
internal class Transition(BeaconChainSpec spec) : Bellatrix.Transition(spec)
{
    public override LegacyFork Fork => LegacyFork.Capella;

    public override LegacyBlock DecodeBlock(ReadOnlySpan<byte> ssz)
    {
        SignedBeaconBlock.Decode(ssz, out SignedBeaconBlock block);
        return new LegacyBlock(block, SszRoots.HashTreeRoot(block.Message!.Body!));
    }

    protected override BeaconStateFulu DecodeStateContainer(ReadOnlySpan<byte> ssz)
    {
        BeaconState.Decode(ssz, out BeaconState state);
        return state;
    }

    protected override void UpgradeFields(BeaconStateFulu state, EpochCache cache)
    {
        ExecutionPayloadHeader header = CopyHeader<ExecutionPayloadHeader>(state.LatestExecutionPayloadHeader!);
        header.WithdrawalsRoot = Hash256.Zero;
        state.LatestExecutionPayloadHeader = header;
        state.NextWithdrawalIndex = 0;
        state.NextWithdrawalValidatorIndex = 0;
        state.HistoricalSummaries = [];
    }

    public override void ProcessPayload(BeaconStateFulu state, Body body, INewPayloadNotifier notifier)
    {
        ProcessWithdrawals(state, body.ExecutionPayload!);
        ProcessExecutionPayload(state, body, notifier);
    }

    public override void ProcessExecutionPayload(BeaconStateFulu state, Body body, INewPayloadNotifier notifier)
    {
        BlockProcessing.ProcessExecutionPayload(state, body, notifier, maxBlobsPerBlock: 0);
        Types.ExecutionPayloadHeader processed = state.LatestExecutionPayloadHeader!;
        ExecutionPayloadHeader header = CopyHeader<ExecutionPayloadHeader>(processed);
        header.WithdrawalsRoot = processed.WithdrawalsRoot;
        state.LatestExecutionPayloadHeader = header;
    }

    /// <summary>Spec <c>process_withdrawals</c>, where only 0x01 credentials are withdrawable.</summary>
    public void ProcessWithdrawals(BeaconStateFulu state, Types.ExecutionPayload payload)
    {
        ReadOnlySpan<Validator> validators = state.ReadOnlyValidators;
        SszWithdrawal[] withdrawals = payload.Withdrawals ?? [];
        ulong epoch = state.GetCurrentEpoch();
        ulong withdrawalIndex = state.NextWithdrawalIndex;
        ulong validatorIndex = state.NextWithdrawalValidatorIndex;
        int expectedCount = 0;
        int bound = Math.Min(validators.Length, Presets.MaxValidatorsPerWithdrawalsSweep);
        for (int i = 0; i < bound && expectedCount < Presets.MaxWithdrawalsPerPayload; i++)
        {
            Validator validator = validators[(int)validatorIndex];
            ulong balance = state.Balances![(int)validatorIndex];
            ulong amount = 0;
            if (validator.HasEth1WithdrawalCredential())
            {
                if (validator.WithdrawableEpoch <= epoch)
                    amount = balance;
                else if (validator.EffectiveBalance == Presets.MaxEffectiveBalance && balance > Presets.MaxEffectiveBalance)
                    amount = balance - Presets.MaxEffectiveBalance;
            }
            if (amount != 0)
            {
                SszWithdrawal withdrawal = expectedCount < withdrawals.Length ? withdrawals[expectedCount] : default;
                if (withdrawal.Index != withdrawalIndex
                    || withdrawal.ValidatorIndex != validatorIndex
                    || withdrawal.Address != new Address(validator.WithdrawalCredentials!)
                    || withdrawal.Amount != amount)
                    throw new BeaconStateException($"Payload withdrawal {expectedCount} does not match the expected withdrawal");
                expectedCount++;
                withdrawalIndex++;
            }
            validatorIndex = (validatorIndex + 1) % (ulong)validators.Length;
        }
        if (withdrawals.Length != expectedCount)
            throw new BeaconStateException($"Payload has {withdrawals.Length} withdrawals, expected {expectedCount}");

        // A sweep visits each validator at most once, so the balances above need no earlier withdrawal of this block taken off.
        foreach (SszWithdrawal withdrawal in withdrawals)
            state.DecreaseBalance((int)withdrawal.ValidatorIndex, withdrawal.Amount);
        state.NextWithdrawalIndex = withdrawalIndex;
        state.NextWithdrawalValidatorIndex = expectedCount == Presets.MaxWithdrawalsPerPayload
            ? (withdrawals[^1].ValidatorIndex + 1) % (ulong)validators.Length
            : (state.NextWithdrawalValidatorIndex + (ulong)Presets.MaxValidatorsPerWithdrawalsSweep) % (ulong)validators.Length;
    }

    public override void ProcessOperations(BeaconStateFulu state, Body body, EpochCache cache, PubkeyCache pubkeys, bool verifySignatures, BlockSignatureBatch? batch)
    {
        base.ProcessOperations(state, body, cache, pubkeys, verifySignatures, batch);
        foreach (SignedBlsToExecutionChange change in body.BlsToExecutionChanges ?? [])
            BlockProcessing.ProcessBlsToExecutionChange(state, change, verifySignatures, batch, Spec);
    }

    public override void ProcessHistoricalUpdate(BeaconStateFulu state) =>
        EpochProcessing.ProcessHistoricalSummariesUpdate(state);
}
