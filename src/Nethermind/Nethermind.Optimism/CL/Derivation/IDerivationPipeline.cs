// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL.Derivation;

public interface IDerivationPipeline
{
    Task<(OptimismPayloadAttributes[], SystemConfig[], L1BlockInfo[])> ConsumeV1Batches(L2Block l2Parent, BatchV1[] batches);
    Task ConsumeV0Batches(BatchV0[] batches);
}
