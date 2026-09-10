// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Config;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.Blocks;

/// <summary>
/// The BFT header rules: constant mix hash and difficulty, validator list (or its absence in contract
/// mode), proposer membership and a quorum of distinct committed seals from validators.
/// </summary>
/// <remarks>Mirrors Besu's <c>QbftBlockHeaderValidationRulesetFactory</c> rules that are not already part of <c>HeaderValidator</c>.</remarks>
public class QbftSealValidator(
    IValidatorProvider validatorProvider,
    QbftBlockInterface blockInterface,
    QbftForksSchedule forksSchedule,
    ILogManager logManager) : ISealValidator
{
    [ThreadStatic]
    private static bool _validatingProposal;

    private readonly ILogger _logger = logManager.GetClassLogger<QbftSealValidator>();

    /// <summary>
    /// While the returned scope is alive on this thread, headers are validated as proposals: every rule
    /// applies except the committed seals, which a proposal does not carry yet (Besu <c>HeaderValidationMode.LIGHT</c>).
    /// </summary>
    public static IDisposable EnterProposalValidation()
    {
        _validatingProposal = true;
        return new ProposalValidationScope();
    }

    private sealed class ProposalValidationScope : IDisposable
    {
        public void Dispose() => _validatingProposal = false;
    }

    public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false) => ValidateParams(parent, header, out _);

    public bool ValidateParams(BlockHeader parent, BlockHeader header, [NotNullWhen(false)] out string? error)
    {
        if (header.MixHash != BftHelpers.ExpectedMixHash)
        {
            return Fail(header, $"mix hash {header.MixHash} is not the BFT constant", out error);
        }

        if (header.Difficulty != UInt256.One)
        {
            return Fail(header, $"difficulty {header.Difficulty} is not 1", out error);
        }

        BftExtraData extraData;
        try
        {
            extraData = blockInterface.GetExtraData(header);
        }
        catch (Exception e) when (e is Serialization.Rlp.RlpException or ArgumentException or IndexOutOfRangeException)
        {
            return Fail(header, $"extra data cannot be decoded: {e.Message}", out error);
        }

        IReadOnlyList<Address> storedValidators = validatorProvider.GetValidatorsAfterBlock(parent);
        if (!ValidateValidators(header, extraData, storedValidators, out error)
            || !ValidateCoinbase(header, storedValidators, out error)
            || (!_validatingProposal && !ValidateCommitSeals(header, extraData, storedValidators, out error)))
        {
            return false;
        }

        error = null;
        return true;
    }

    public bool ValidateSeal(BlockHeader header, bool force)
    {
        header.Author ??= header.Beneficiary;
        return true;
    }

    private bool ValidateValidators(BlockHeader header, BftExtraData extraData, IReadOnlyList<Address> storedValidators, [NotNullWhen(false)] out string? error)
    {
        if (forksSchedule.GetFork((long)header.Number, header.Timestamp).IsValidatorContractMode)
        {
            if (extraData.Validators.Count != 0)
            {
                return Fail(header, "validators in extra data expected to be empty in contract mode", out error);
            }

            if (extraData.Vote is not null)
            {
                return Fail(header, "vote in extra data expected to be empty in contract mode", out error);
            }

            error = null;
            return true;
        }

        IReadOnlyList<Address> reported = extraData.Validators;
        for (int i = 1; i < reported.Count; i++)
        {
            if (reported[i - 1].CompareTo(reported[i]) >= 0)
            {
                return Fail(header, "validators are not sorted in ascending order", out error);
            }
        }

        if (!SameSequence(reported, storedValidators))
        {
            return Fail(header, $"incorrect validators, expected [{string.Join(", ", storedValidators)}] but got [{string.Join(", ", reported)}]", out error);
        }

        error = null;
        return true;
    }

    private bool ValidateCoinbase(BlockHeader header, IReadOnlyList<Address> storedValidators, [NotNullWhen(false)] out string? error)
    {
        Address proposer = QbftBlockInterface.GetProposer(header);
        if (!storedValidators.ContainsAddress(proposer))
        {
            return Fail(header, $"block proposer {proposer} is not a member of the validators", out error);
        }

        error = null;
        return true;
    }

    private bool ValidateCommitSeals(BlockHeader header, BftExtraData extraData, IReadOnlyList<Address> storedValidators, [NotNullWhen(false)] out string? error)
    {
        Address[] committers = BftBlockHashing.RecoverCommitters(header, extraData, blockInterface.CodecFor(header));
        HashSet<Address> distinct = [.. committers];
        if (distinct.Count != committers.Length)
        {
            return Fail(header, "duplicated seals found in header", out error);
        }

        int required = BftHelpers.CalculateRequiredValidatorQuorum(storedValidators.Count);
        if (distinct.Count < required)
        {
            return Fail(header, $"insufficient committers to seal block (required {required}, received {distinct.Count})", out error);
        }

        foreach (Address committer in distinct)
        {
            if (!storedValidators.ContainsAddress(committer))
            {
                return Fail(header, $"committer {committer} is not in the locally maintained validator list", out error);
            }
        }

        error = null;
        return true;
    }

    private bool Fail(BlockHeader header, string reason, [NotNullWhen(false)] out string? error)
    {
        error = $"Invalid BFT block header {header.ToString(BlockHeader.Format.Short)}: {reason}";
        if (_logger.IsInfo) _logger.Info(error);
        return false;
    }

    private static bool SameSequence(IReadOnlyList<Address> left, IReadOnlyList<Address> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i]) return false;
        }

        return true;
    }
}
