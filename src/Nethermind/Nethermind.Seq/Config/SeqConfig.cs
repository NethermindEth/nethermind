// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Seq.Config
{
    public class SeqConfig : ISeqConfig
    {
        public string MinLevel { get; set; } = "Off";
        public string ServerUrl { get; set; } = "http://localhost:5341";
        public string ApiKey { get; set; } = string.Empty;
    }
}
