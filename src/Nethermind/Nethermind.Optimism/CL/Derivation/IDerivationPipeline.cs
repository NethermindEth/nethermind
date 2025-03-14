// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Optimism.CL.Decoding;

namespace Nethermind.Optimism.CL.Derivation;

public interface IDerivationPipeline
{
    Task Run(CancellationToken token);
    ChannelWriter<(L2Block L2Parent, BatchV1 Batch)> BatchesForProcessing { get; }
}
