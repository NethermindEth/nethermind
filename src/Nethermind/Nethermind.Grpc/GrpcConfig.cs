// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Grpc
{
    public class GrpcConfig : IGrpcConfig
    {
        public bool Enabled { get; set; }
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 50000;
        public bool ProducerEnabled { get; set; } = false;
    }
}
