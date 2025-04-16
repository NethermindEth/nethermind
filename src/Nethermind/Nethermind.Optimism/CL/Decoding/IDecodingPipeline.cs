// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Nethermind.Optimism.CL.Decoding;

// Gets da data and creates Batches
public interface IDecodingPipeline
{
    Task Run(CancellationToken token);
    ChannelWriter<byte[]> DaDataWriter { get; }
    ChannelReader<BatchV1> DecodedBatchesReader { get; }
}
