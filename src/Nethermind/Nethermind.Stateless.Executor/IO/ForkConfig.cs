// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct ForkConfig
{
    public SszForkActivation Activation { get; set; }

    public static ForkConfig From(BlockHeader header, ISpecProvider specProvider)
    {
        ForkActivation forkActivation = new(header.Number, header.Timestamp);

        for (int i = specProvider.TransitionActivations.Length - 1; i >= 0; i--)
        {
            ForkActivation activation = specProvider.TransitionActivations[i];

            if (activation <= forkActivation)
            {
                forkActivation = activation;
                break;
            }
        }

        return new()
        {
            Activation = SszForkActivation.From(forkActivation)
        };
    }
}
