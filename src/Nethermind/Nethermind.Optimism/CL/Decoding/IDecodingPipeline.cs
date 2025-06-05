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
    ChannelWriter<DaDataSource> DaDataWriter { get; }
    // L1BatchOrigin - block at witch we fully reconstructed Batch
    ChannelReader<(BatchV1 Batch, ulong L1BatchOrigin)> DecodedBatchesReader { get; }
    Task Reset(CancellationToken token);
}

public class DaDataSource
{
    public required ulong DataOrigin { get; init; }
    public required byte[] Data { get; init; }
    public required DaDataType DataType { get; init; }
}

public enum DaDataType
{
    Blob,
    Calldata
}
