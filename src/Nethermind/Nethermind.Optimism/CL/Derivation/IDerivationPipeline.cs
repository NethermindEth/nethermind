// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Optimism.CL.Decoding;

namespace Nethermind.Optimism.CL.Derivation;

public interface IDerivationPipeline
{
    IAsyncEnumerable<PayloadAttributesRef> DerivePayloadAttributes(L2Block l2Parent, BatchV1 batch, CancellationToken token);
}
