// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.HealthChecks;

public interface IHealthChecksConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable the health check.", DefaultValue = "false")]
    public bool Enabled { get; set; }

    [ConfigItem(Description = "Whether to enable web hooks.", DefaultValue = "false")]
    public bool WebhooksEnabled { get; set; }

    [ConfigItem(Description = "The URL slug the health checks service is exposed at.", DefaultValue = "/health")]
    public string Slug { get; set; }

    [ConfigItem(Description = "The web hook URL.", DefaultValue = "null")]
    public string WebhooksUri { get; set; }

    [ConfigItem(Description = "An escaped JSON paylod to be sent to the web hook on failure.",
        DefaultValue = """
            ```json
            {
              "attachments": [
                {
                  "color": "#FFCC00",
                  "pretext": "Health Check Status :warning:",
                  "fields": [
                    {
                      "title": "Details",
                      "value": "More details available at /healthchecks-ui",
                      "short": false
                    },
                    {
                      "title": "Description",
                      "value": "[[DESCRIPTIONS]]",
                      "short": false
                    }
                  ]
                }
              ]
            }
            ```
            """)]
    public string WebhooksPayload { get; set; }

    [ConfigItem(Description = "An escaped JSON paylod to be sent to the web hook on recovery.",
        DefaultValue = """
            ```json
            {
              "attachments": [
                {
                  "color": "#36a64f",
                  "pretext": "Health Check Status :+1:",
                  "fields": [
                    {
                      "title": "Details",
                      "value": "More details available at /healthchecks-ui",
                      "short": false
                    },
                    {
                      "title": "description",
                      "value": "The HealthCheck `[[LIVENESS]]` is recovered. Everything is up and running.",
                      "short": false
                    }
                  ]
                }
              ]
            }
            ```
            """)]
    public string WebhooksRestorePayload { get; set; }

    [ConfigItem(Description = "Whether to enable the health checks UI.", DefaultValue = "false")]
    public bool UIEnabled { get; set; }

    [ConfigItem(Description = "The health check updates polling interval, in seconds.", DefaultValue = "5")]
    public int PollingInterval { get; set; }

    [ConfigItem(Description = "The max interval, in seconds, in which the block processing is assumed healthy.", DefaultValue = "null")]
    public ulong? MaxIntervalWithoutProcessedBlock { get; set; }

    [ConfigItem(Description = "The max interval, in seconds, in which the block production is assumed healthy.", DefaultValue = "null")]
    public ulong? MaxIntervalWithoutProducedBlock { get; set; }

    [ConfigItem(Description = "The max request interval, in seconds, in which the consensus client is assumed healthy.", DefaultValue = "300")]
    public int MaxIntervalClRequestTime { get; set; }

    [ConfigItem(Description = "The percentage of available disk space below which a warning is displayed. `0` to disable.", DefaultValue = "5")]
    public float LowStorageSpaceWarningThreshold { get; set; }

    [ConfigItem(Description = "The percentage of available disk space below which Nethermind shuts down. `0` to disable.", DefaultValue = "1")]
    public float LowStorageSpaceShutdownThreshold { get; set; }

    [ConfigItem(Description = "Whether to check for low disk space on startup and suspend until enough space is available.", DefaultValue = "false")]
    public bool LowStorageCheckAwaitOnStartup { get; set; }
}
