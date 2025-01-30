// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL.Derivation;

public interface IDerivationPipeline
{
    Task ConsumeV1Batches(BatchV1[] batches);
    Task ConsumeV0Batches(BatchV0[] batches);
    event Action<OptimismPayloadAttributes[], ulong>? OnL2BlocksDerived;
}
