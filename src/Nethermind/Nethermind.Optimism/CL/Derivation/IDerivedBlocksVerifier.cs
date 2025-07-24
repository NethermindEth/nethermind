// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL.Derivation;

public interface IDerivedBlocksVerifier
{
    public bool ComparePayloadAttributes(OptimismPayloadAttributes expected, OptimismPayloadAttributes actual, ulong blockNumber);
}
