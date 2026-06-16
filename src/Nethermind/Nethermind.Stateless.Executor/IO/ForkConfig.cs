// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct ForkConfig
{
    public ulong Fork { get; set; }

    public SszForkActivation Activation { get; set; }

    [SszList(1)]
    public BlobSchedule[] BlobSchedule { get; set; }

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

        IReleaseSpec spec = specProvider.GetSpec(forkActivation);

        return new()
        {
            Fork = ForkIndexHelper.GetForkIndexByName(spec.Name),
            Activation = SszForkActivation.From(forkActivation),
            BlobSchedule =
            [
                new()
                {
                    Target = spec.TargetBlobCount,
                    Max = spec.MaxBlobCount,
                    BaseFeeUpdateFraction = (ulong)spec.BlobBaseFeeUpdateFraction
                }
            ]
        };
    }
}
