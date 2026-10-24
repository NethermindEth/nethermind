// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.Spec;

/// <summary>Fork-choice deadlines as basis-point fractions of the network's slot duration.</summary>
public static class GloasTiming
{
    public const ulong ProposerReorgCutoffBps = 1_667;
    public const ulong AttestationDueBps = 3_333;
    public const ulong AttestationDueBpsGloas = 2_500;
    public const ulong PayloadAttestationDueBps = 7_500;
}
