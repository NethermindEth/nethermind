// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Grpc
{
    [ConfigCategory(DisabledForCli = true, HiddenFromDocs = true)]
    public interface IGrpcConfig : IConfig
    {
        [ConfigItem(Description = "If 'false' then it disables gRPC protocol", DefaultValue = "false")]
        bool Enabled { get; }

        [ConfigItem(Description = "An address of the host under which gRPC will be running", DefaultValue = "localhost")]
        string Host { get; }

        [ConfigItem(Description = "Port of the host under which gRPC will be exposed", DefaultValue = "50000")]
        int Port { get; }
    }
}
