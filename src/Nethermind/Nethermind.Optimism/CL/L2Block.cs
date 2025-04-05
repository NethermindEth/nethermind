// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Optimism.CL.Derivation;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL;

public class L2Block
{
    public required Hash256 Hash { get; init; }
    public required Hash256 ParentHash { get; init; }
    public required PayloadAttributesRef PayloadAttributesRef { get; init; }
    public ulong Number => PayloadAttributesRef.Number;
    public OptimismPayloadAttributes PayloadAttributes => PayloadAttributesRef.PayloadAttributes;
    public SystemConfig SystemConfig => PayloadAttributesRef.SystemConfig;
    public L1BlockInfo L1BlockInfo => PayloadAttributesRef.L1BlockInfo;
}
