// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal static class NativeNetworkProfile
{
    private const int Words = 15;
    private static readonly ValueHash256 Domain = ValueKeccak.Compute("EEZ_ROLLUPX_NETWORK_PROFILE_V1"u8);
    private static readonly ValueHash256 KeyDomain = ValueKeccak.Compute("EEZ_ROLLUPX_VERIFICATION_KEY_V1"u8);

    internal static ValueHash256 Compute(in NativeOfflineProfile.Inputs input)
    {
        Span<byte> encoded = stackalloc byte[Words * AbiWord.Size];
        Write(encoded, in input);
        return ValueKeccak.Compute(encoded);
    }

    internal static byte[] Encode(in NativeOfflineProfile.Inputs input)
    {
        byte[] encoded = new byte[Words * AbiWord.Size];
        Write(encoded, in input);
        return encoded;
    }

    internal static ValueHash256 VerificationKey(in ValueHash256 networkHash, Address registry, Address proofSystem, Address attester)
    {
        Span<byte> encoded = stackalloc byte[5 * AbiWord.Size];
        AbiWriter writer = new(encoded);
        writer.Write(KeyDomain);
        writer.Write(networkHash);
        writer.Write(registry);
        writer.Write(proofSystem);
        writer.Write(attester);
        return ValueKeccak.Compute(encoded);
    }

    private static void Write(Span<byte> encoded, in NativeOfflineProfile.Inputs input)
    {
        if (input.ValidationVersion != 7) throw new ArgumentException("Network keys require validation version 7.");
        AbiWriter writer = new(encoded);
        writer.Write(Domain);
        writer.Write(input.ValidationVersion);
        writer.Write(EezConstants.L1HistoryAddress);
        writer.Write((ulong)NativeHistoryCall.MaxHashes);
        writer.Write(EezConstants.L1HistoryTxGasCap);
        writer.Write(NativeProxyHistory.RuntimeHash);
        writer.Write(input.L1ChainId);
        writer.Write(input.L2ChainId);
        writer.Write(input.GenesisHash);
        writer.Write(input.GenesisTimestamp);
        writer.Write(input.BlockInterval);
        writer.Write((ulong)input.SyncInterval);
        writer.Write((ulong)input.UpdateOffset);
        writer.Write(input.L1ConfigHash);
        writer.Write(input.FeeCollector);
    }
}
