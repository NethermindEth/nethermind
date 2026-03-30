// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Specs.Forks;

public class Amsterdam() : NamedReleaseSpec<Amsterdam>(BPO5.Instance)
{
    public override void Apply(ReleaseSpec spec)
    {
        spec.Name = "Amsterdam";
        spec.IsEip7708Enabled = true;
        spec.IsEip7778Enabled = true;
        spec.IsEip7843Enabled = true;
        spec.IsEip7928Enabled = true;
        spec.IsEip7954Enabled = true;
        spec.MaxCodeSize = CodeSizeConstants.MaxCodeSizeEip7954;
        spec.IsEip8024Enabled = true;
        spec.IsEip8037Enabled = true;
    }

    public static IReleaseSpec NoEip8037Instance { get; } = new Amsterdam { IsEip8037Enabled = false };
}
