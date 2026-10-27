// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Eez.Execution.Settlement.RollupX;

namespace Nethermind.Eez.Attester;

/// <summary>The owned native chain configuration shared by one session registry and backend.</summary>
internal sealed class NativeStreamingProfile
{
    internal NativeGenesisSnapshot Genesis { get; }
    internal NativeL1HeaderRules Headers { get; }

    internal NativeStreamingProfile(NativeGenesisSnapshot genesis, NativeL1Rules l1)
    {
        if (!genesis.Rules.UsesNativeContractV3) throw new ArgumentException("Native sessions require the current execution contract.", nameof(genesis));
        _ = l1.Profile(genesis);
        Genesis = genesis;
        Headers = l1.Headers;
    }
}
