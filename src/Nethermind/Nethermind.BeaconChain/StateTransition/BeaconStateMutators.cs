// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition;

/// <summary>
/// Consensus-specs mutators over <see cref="BeaconStateFulu"/> (Electra/Fulu semantics).
/// </summary>
/// <remarks>
/// <see cref="Validator"/> instances are treated as immutable: mutators install a modified copy
/// (see <see cref="Clone"/>) instead of writing through a possibly shared instance.
/// </remarks>
public static partial class BeaconStateMutators
{
    /// <summary>Returns a field-by-field copy, the supported way to "mutate" a validator in the registry.</summary>
    public static Validator Clone(this Validator validator) => new()
    {
        Pubkey = validator.Pubkey,
        WithdrawalCredentials = validator.WithdrawalCredentials,
        EffectiveBalance = validator.EffectiveBalance,
        Slashed = validator.Slashed,
        ActivationEligibilityEpoch = validator.ActivationEligibilityEpoch,
        ActivationEpoch = validator.ActivationEpoch,
        ExitEpoch = validator.ExitEpoch,
        WithdrawableEpoch = validator.WithdrawableEpoch,
    };

    public static partial void IncreaseBalance(this BeaconStateFulu state, int index, ulong delta);
    public static partial void DecreaseBalance(this BeaconStateFulu state, int index, ulong delta);
    public static partial ulong ComputeExitEpochAndUpdateChurn(this BeaconStateFulu state, ulong exitBalance, EpochCache cache);
    public static partial ulong ComputeConsolidationEpochAndUpdateChurn(this BeaconStateFulu state, ulong consolidationBalance, EpochCache cache);
    public static partial void InitiateValidatorExit(this BeaconStateFulu state, int index, EpochCache cache);
    private static partial ulong CheckedEpochSum(ulong epoch, ulong delta);

    /// <summary>
    /// Spec <c>add_validator_to_registry</c> (Electra): appends a deposit-derived validator and its
    /// per-validator list entries to the state.
    /// </summary>
    public static void AddValidatorToRegistry(this BeaconStateFulu state, BlsPublicKey pubkey, Hash256 withdrawalCredentials, ulong amount)
    {
        state.Validators = [.. state.Validators!, GetValidatorFromDeposit(pubkey, withdrawalCredentials, amount)];
        state.Balances = [.. state.Balances!, amount];
        state.PreviousEpochParticipation = [.. state.PreviousEpochParticipation!, 0];
        state.CurrentEpochParticipation = [.. state.CurrentEpochParticipation!, 0];
        state.InactivityScores = [.. state.InactivityScores!, 0UL];
    }

    /// <summary>Spec <c>get_validator_from_deposit</c> (Electra).</summary>
    private static Validator GetValidatorFromDeposit(BlsPublicKey pubkey, Hash256 withdrawalCredentials, ulong amount)
    {
        Validator validator = new()
        {
            Pubkey = pubkey,
            WithdrawalCredentials = withdrawalCredentials,
            EffectiveBalance = 0,
            Slashed = false,
            ActivationEligibilityEpoch = Presets.FarFutureEpoch,
            ActivationEpoch = Presets.FarFutureEpoch,
            ExitEpoch = Presets.FarFutureEpoch,
            WithdrawableEpoch = Presets.FarFutureEpoch,
        };
        // [Modified in Electra:EIP7251] The cap depends on the credential type.
        validator.EffectiveBalance = Math.Min(amount - amount % Presets.EffectiveBalanceIncrement, validator.GetMaxEffectiveBalance());
        return validator;
    }

    public static partial void SlashValidator(this BeaconStateFulu state, int slashedIndex, EpochCache cache, int? whistleblowerIndex = null);
}
