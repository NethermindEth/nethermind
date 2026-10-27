// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>The manager's native offline profile ABI commitment and its L1 rule epoch identity.</summary>
/// <remarks>Encoding does not establish that profile fields match locally authenticated genesis and L1 rules.</remarks>
internal static class NativeOfflineProfile
{
    internal const int MaxRuleEpochs = 64;
    private const int OuterWords = 6;
    private const int ProfileWords = 11;
    private static readonly ValueHash256 Domain = ValueKeccak.Compute("EEZ_ROLLUPX_OFFLINE_PROFILE"u8);
    private static readonly ValueHash256 RulesDomain = ValueKeccak.Compute("EEZ_ROLLUPX_L1_RULES_V1"u8);

    internal static ValueHash256 Compute(in Inputs input, ReadOnlySpan<ulong> ruleTimes) => ValueKeccak.Compute(Encode(in input, ruleTimes));

    internal static void Validate(in Inputs input, ReadOnlySpan<ulong> ruleTimes, in UInt256 l1ChainId, in ValueHash256 historyCodeHash)
    {
        ValidateEpochs(ruleTimes);
        if (input.L1ChainId.IsZero || input.L1ChainId != l1ChainId || input.L2ChainId.IsZero || input.GenesisHash == default ||
            input.L1ConfigHash == default || historyCodeHash == default || input.HistoryCodeHash != historyCodeHash ||
            input.FeeCollector == Address.Zero || input.FeeCollector == Address.SystemUser || input.FeeCollector == EezConstants.RollupXEezl2Address)
            throw new ArgumentException("Invalid native offline network profile.");
        _ = new NativeHistorySchedule(input.GenesisTimestamp, input.BlockInterval, input.SyncInterval, input.UpdateOffset);
    }

    internal static byte[] Encode(in Inputs input, ReadOnlySpan<ulong> ruleTimes)
    {
        ValidateEpochs(ruleTimes);
        byte[] encoded = new byte[(OuterWords + ProfileWords + 1 + ruleTimes.Length) * AbiWord.Size];
        AbiWriter writer = new(encoded);
        writer.Write(Domain);
        writer.Write(RollupXValidationIdentity.Version);
        writer.Write(EezConstants.L1HistoryAddress);
        writer.Write((ulong)NativeHistoryCall.MaxHashes);
        writer.Write(EezConstants.L1HistoryTxGasCap);
        writer.WriteOffset(OuterWords * AbiWord.Size);
        writer.Write(input.L1ChainId);
        writer.Write(input.L2ChainId);
        writer.Write(input.GenesisHash);
        writer.Write(input.GenesisTimestamp);
        writer.Write(input.BlockInterval);
        writer.Write((ulong)input.SyncInterval);
        writer.Write((ulong)input.UpdateOffset);
        writer.Write(input.L1ConfigHash);
        writer.Write(input.HistoryCodeHash);
        writer.Write(input.FeeCollector);
        writer.WriteOffset(ProfileWords * AbiWord.Size);
        writer.Write((ulong)ruleTimes.Length);
        foreach (ulong time in ruleTimes) writer.Write(time);
        return encoded;
    }

    internal static ValueHash256 RulesId(in ValueHash256 l1ConfigHash, ReadOnlySpan<ulong> ruleTimes, ulong timestamp)
    {
        ValidateEpochs(ruleTimes);
        if (l1ConfigHash == default) throw new ArgumentException("Missing effective L1 rule configuration hash.");
        int index = ruleTimes.Length - 1;
        while (ruleTimes[index] > timestamp) index--;
        Span<byte> bytes = stackalloc byte[3 * AbiWord.Size];
        AbiWriter writer = new(bytes);
        writer.Write(RulesDomain);
        writer.Write(l1ConfigHash);
        writer.Write(ruleTimes[index]);
        return ValueKeccak.Compute(bytes);
    }

    private static void ValidateEpochs(ReadOnlySpan<ulong> ruleTimes)
    {
        if (ruleTimes.IsEmpty || ruleTimes.Length > MaxRuleEpochs || ruleTimes[0] != 0)
            throw new ArgumentException("Invalid L1 rule epochs.");
        for (int index = 1; index < ruleTimes.Length; index++)
            if (ruleTimes[index] <= ruleTimes[index - 1]) throw new ArgumentException("L1 rule epochs must strictly increase.");
    }

    internal readonly record struct Inputs(UInt256 L1ChainId, UInt256 L2ChainId, ValueHash256 GenesisHash, ulong GenesisTimestamp,
        ulong BlockInterval, uint SyncInterval, uint UpdateOffset, ValueHash256 L1ConfigHash, ValueHash256 HistoryCodeHash, Address FeeCollector);
}
