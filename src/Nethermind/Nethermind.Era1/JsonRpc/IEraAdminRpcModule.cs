// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Era1.JsonRpc;

[RpcModule(ModuleType.Admin)]
public interface IEraAdminRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Exports a range of historic block in era1 format.",
    EdgeCaseHint = "",
    ExampleResponse = "\"Export task started.\"",
    IsImplemented = true)]
    Task<ResultWrapper<string>> admin_exportHistory(
        [JsonRpcParameter(Description = "Destination path to export to.", ExampleValue = "/tmp/eraexportdir")]
        string destinationPath,
        [JsonRpcParameter(Description = "Start block to export from.", ExampleValue = "0")]
        int from,
        [JsonRpcParameter(Description = "Last block to export to. Set to 0 to export to head.", ExampleValue = "1000000")]
        int to
    );

    [JsonRpcMethod(Description = "Import a range of historic block from era1 directory.",
    EdgeCaseHint = "",
    ExampleResponse = "\"Export task started.\"",
    IsImplemented = true)]
    Task<ResultWrapper<string>> admin_importHistory(
        [JsonRpcParameter(Description = "Source path to import from.", ExampleValue = "/tmp/eradir")]
        string sourcePath,
        [JsonRpcParameter(Description = "Start block to import from the era directory. Set to 0 to import from the first available block.", ExampleValue = "0")]
        int from = 0,
        [JsonRpcParameter(Description = "End block to import from the era directory. Set to 0 to import until last block.", ExampleValue = "0")]
        int to = 0,
        [JsonRpcParameter(Description = "Accumulator file to trust. Set to null to trust the era archive without accumulator file verification.", ExampleValue = "null")]
        string? accumulatorFile = null
    );
}
