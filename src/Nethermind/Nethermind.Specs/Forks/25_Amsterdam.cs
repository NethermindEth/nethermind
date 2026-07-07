// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Amsterdam() : NamedReleaseSpec<Amsterdam>(BPO2.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Amsterdam";
        spec.IsEip2780Enabled = true;
        spec.IsEip7976Enabled = true;
        spec.IsEip7981Enabled = true;
        spec.IsEip7708Enabled = true;
        spec.IsEip7778Enabled = true;
        spec.IsEip7843Enabled = true;
        spec.IsEip7928Enabled = true;
        spec.IsEip7954Enabled = true;
        spec.MaxCodeSize = CodeSizeConstants.MaxCodeSizeEip7954;
        spec.IsEip8024Enabled = true;
        spec.IsEip8037Enabled = true;
        spec.IsEip8038Enabled = true;
        spec.IsEip8246Enabled = true;
        spec.IsEip8282Enabled = true;
        spec.EngineApiNewPayloadVersion = EngineApiVersions.NewPayload.V5;
        spec.EngineApiGetPayloadVersion = EngineApiVersions.GetPayload.V6;
        spec.EngineApiForkchoiceVersion = EngineApiVersions.Fcu.V4;
        spec.EngineApiPayloadBodiesByHashVersion = EngineApiVersions.PayloadBodiesByHash.V2;
        spec.EngineApiPayloadBodiesByRangeVersion = EngineApiVersions.PayloadBodiesByRange.V2;
    }

    /// <summary>
    /// Test-only Amsterdam variant with the EIP-8037 two-dimensional gas model disabled.
    /// </summary>
    /// <remarks>
    /// Intentionally inconsistent (EIP-8038 access costs on the one-dimensional gas model);
    /// exists only to isolate EIP-8037 behavior in tests.
    /// </remarks>
    public static IReleaseSpec NoEip8037Instance { get; } = new Amsterdam { IsEip8037Enabled = false };
}
