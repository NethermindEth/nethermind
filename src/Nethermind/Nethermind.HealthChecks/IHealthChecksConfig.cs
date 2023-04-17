// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.HealthChecks
{
    public interface IHealthChecksConfig : IConfig
    {
        [ConfigItem(Description = "If 'true' then Health Check endpoints is enabled at /health", DefaultValue = "false")]
        public bool Enabled { get; set; }

        [ConfigItem(Description = "If 'true' then Webhooks can be configured", DefaultValue = "false")]
        public bool WebhooksEnabled { get; set; }

        [ConfigItem(Description = "The URL slug on which Healthchecks service will be exposed", DefaultValue = "/health")]
        public string Slug { get; set; }

        [ConfigItem(Description = "The Webhooks endpoint e.g. Slack WebHooks", DefaultValue = "null")]
        public string WebhooksUri { get; set; }

        [ConfigItem(Description = "Payload is the json payload that will be send on Failure and must be escaped.", DefaultValue = "{\"attachments\":[{\"color\":\"#FFCC00\",\"pretext\":\"Health Check Status :warning:\",\"fields\":[{\"title\":\"Details\",\"value\":\"More details available at `/healthchecks-ui`\",\"short\":false},{\"title\":\"Description\",\"value\":\"[[DESCRIPTIONS]]\",\"short\":false}]}]}")]
        public string WebhooksPayload { get; set; }

        [ConfigItem(Description = "RestorePayload is the json payload that will be send on Recovery and must be escaped.", DefaultValue = "{\"attachments\":[{\"color\":\"#36a64f\",\"pretext\":\"Health Check Status :+1:\",\"fields\":[{\"title\":\"Details\",\"value\":\"`More details available at /healthchecks-ui`\",\"short\":false},{\"title\":\"description\",\"value\":\"The HealthCheck `[[LIVENESS]]` is recovered. All is up and running\",\"short\":false}]}]}")]
        public string WebhooksRestorePayload { get; set; }

        [ConfigItem(Description = "If 'true' then HealthChecks UI will be avaiable at /healthchecks-ui", DefaultValue = "false")]
        public bool UIEnabled { get; set; }

        [ConfigItem(Description = "Configures the UI to poll for healthchecks updates (in seconds)", DefaultValue = "5")]
        public int PollingInterval { get; set; }

        [ConfigItem(Description = "Max interval in seconds in which we assume that node processing blocks in a healthy way", DefaultValue = "null")]
        public ulong? MaxIntervalWithoutProcessedBlock { get; set; }

        [ConfigItem(Description = "Max interval in seconds in which we assume that node producing blocks in a healthy way", DefaultValue = "null")]
        public ulong? MaxIntervalWithoutProducedBlock { get; set; }

        [ConfigItem(Description = "Max request interval in which we assume that CL works in a healthy way", DefaultValue = "300")]
        public int MaxIntervalClRequestTime { get; set; }

        [ConfigItem(Description = "Percentage of available disk space below which a warning will be displayed. Zero to disable.", DefaultValue = "5")]
        public float LowStorageSpaceWarningThreshold { get; set; }

        [ConfigItem(Description = "Percentage of available disk space below which node will shutdown. Zero to disable.", DefaultValue = "1")]
        public float LowStorageSpaceShutdownThreshold { get; set; }

        [ConfigItem(Description = "Free disk space check on startup will pause node initalization until enough space is available.", DefaultValue = "false")]
        public bool LowStorageCheckAwaitOnStartup { get; set; }
    }
}
