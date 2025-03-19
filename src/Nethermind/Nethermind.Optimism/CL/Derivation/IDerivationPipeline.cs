// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Optimism.CL.Decoding;

namespace Nethermind.Optimism.CL.Derivation;

public interface IDerivationPipeline
{
    Task<PayloadAttributesRef[]> DerivePayloadAttributes(L2Block l2Parent, BatchV1 batch, CancellationToken token);
}
