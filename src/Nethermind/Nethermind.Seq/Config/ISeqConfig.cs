// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Seq.Config
{
    [ConfigCategory(Description = "Configuration of the Prometheus + Grafana metrics publication. Documentation of the required setup is not yet ready (but the metrics do work and are used by the dev team)")]
    public interface ISeqConfig : IConfig
    {
        [ConfigItem(Description = "Minimal level of log events which will be sent to Seq instance.", DefaultValue = "Off")]
        string MinLevel { get; }

        [ConfigItem(Description = "Seq instance URL.", DefaultValue = "\"http://localhost:5341")]
        string ServerUrl { get; }

        [ConfigItem(Description = "API key used for log events ingestion to Seq instance", DefaultValue = "")]
        string ApiKey { get; }
    }
}
