// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>The exact conditional manager context for a native settlement and its later inclusion check.</summary>
/// <remarks>Local profile/deployment authentication and on-chain context equality remain required before proof acceptance.</remarks>
internal sealed class NativeOfflineContext
{
    private static readonly ValueHash256 Domain = ValueKeccak.Compute("EEZ_ROLLUPX_L1_HISTORY_V1"u8);
    private readonly NativeOfflineProfile.Inputs _profile;
    private readonly ulong[] _ruleTimes;
    private readonly byte[] _customData;

    internal NativeEvidenceAnchor Anchor { get; }
    internal ulong Selector => Anchor.Selector;
    internal ulong RollupId { get; }
    internal ValueHash256 AcceptedL2BlockHash { get; }
    internal ValueHash256 ProfileHash { get; }
    internal ValueHash256 RulesId { get; }
    internal ReadOnlySpan<byte> CustomData => _customData;

    internal NativeOfflineContext(in NativeOfflineProfile.Inputs profile, ReadOnlySpan<ulong> ruleTimes,
        Address registry, Address manager, ulong rollupId, in ValueHash256 acceptedL2BlockHash,
        in NativeEvidenceAnchor anchor, NativeL1Header endpoint)
    {
        if (ruleTimes.Length > NativeOfflineProfile.MaxRuleEpochs) throw new ArgumentException("L1 rule epoch limit exceeded.", nameof(ruleTimes));
        _ruleTimes = ruleTimes.ToArray();
        _profile = profile;
        NativeOfflineProfile.Validate(in _profile, _ruleTimes, _profile.L1ChainId, _profile.HistoryCodeHash);
        if (registry == Address.Zero || manager == Address.Zero || rollupId == 0 || acceptedL2BlockHash == default ||
            anchor.Hash == default || anchor.Number == ulong.MaxValue || endpoint.Number != anchor.Number || endpoint.Hash != anchor.Hash ||
            (anchor.TargetTimestamp is { } target ? target <= endpoint.Timestamp : anchor.Number == 0))
            throw NativeL1Header.Invalid("invalid offline deployment, accepted block or anchor");
        Anchor = anchor;
        RollupId = rollupId;
        AcceptedL2BlockHash = acceptedL2BlockHash;
        ProfileHash = NativeOfflineProfile.Compute(in profile, _ruleTimes);
        RulesId = NativeOfflineProfile.RulesId(profile.L1ConfigHash, _ruleTimes, anchor.TargetTimestamp ?? endpoint.Timestamp);
        _customData = new byte[(anchor.TargetTimestamp.HasValue ? 13 : 12) * AbiWord.Size];
        AbiWriter writer = new(_customData);
        writer.Write(Domain);
        writer.Write(profile.L1ChainId);
        writer.Write(registry);
        writer.Write(manager);
        writer.Write(rollupId);
        writer.Write(profile.L2ChainId);
        writer.Write(ProfileHash);
        writer.Write(RulesId);
        writer.Write(acceptedL2BlockHash);
        writer.Write(anchor.TargetTimestamp.HasValue ? 1UL : 2UL);
        writer.Write(anchor.Number);
        writer.Write(anchor.Hash);
        if (anchor.TargetTimestamp is { } timestamp) writer.Write(timestamp);
    }

    internal NativeLiveSource LiveSource()
    {
        if (Anchor.TargetTimestamp is null || _profile.L2ChainId > ulong.MaxValue)
            throw NativeL1Header.Invalid("historical context or unsupported chain ID cannot authorize native cross-chain actions");
        return new NativeLiveSource((ulong)_profile.L2ChainId, Anchor.Hash);
    }

    internal void ValidateInclusion(NativeL1Header inclusion)
    {
        if (Anchor.TargetTimestamp is { } timestamp)
        {
            if (inclusion.Number != Anchor.Number + 1 || inclusion.ParentHash != Anchor.Hash || inclusion.Timestamp != timestamp)
                throw NativeL1Header.Invalid("actual-parent inclusion mismatch");
        }
        else if (inclusion.Number <= Anchor.Number || inclusion.Number - Anchor.Number > 256)
            throw NativeL1Header.Invalid("historical inclusion anchor unavailable");
        if (NativeOfflineProfile.RulesId(_profile.L1ConfigHash, _ruleTimes, inclusion.Timestamp) != RulesId)
            throw NativeL1Header.Invalid("inclusion crosses an L1 rule epoch");
    }
}
