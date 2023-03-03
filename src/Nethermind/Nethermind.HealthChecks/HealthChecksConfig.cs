// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.HealthChecks
{
    public class HealthChecksConfig : IHealthChecksConfig
    {
        public bool Enabled { get; set; } = false;
        public bool WebhooksEnabled { get; set; } = false;
        public string Slug { get; set; } = "/health";
        public int PollingInterval { get; set; } = 5;
        public string WebhooksUri { get; set; } = null;
        public string WebhooksPayload { get; set; } = "{\"attachments\":[{\"color\":\"#FFCC00\",\"pretext\":\"Health Check Status :warning:\",\"fields\":[{\"title\":\"Details\",\"value\":\"More details available at `/healthchecks-ui`\",\"short\":false},{\"title\":\"Description\",\"value\":\"[[DESCRIPTIONS]]\",\"short\":false}]}]}";
        public string WebhooksRestorePayload { get; set; } = "{\"attachments\":[{\"color\":\"#36a64f\",\"pretext\":\"Health Check Status :+1:\",\"fields\":[{\"title\":\"Details\",\"value\":\"`More details available at /healthchecks-ui`\",\"short\":false},{\"title\":\"description\",\"value\":\"The HealthCheck `[[LIVENESS]]` is recovered. All is up and running\",\"short\":false}]}]}";
        public bool UIEnabled { get; set; } = false;

        public ulong? MaxIntervalWithoutProcessedBlock { get; set; }

        public ulong? MaxIntervalWithoutProducedBlock { get; set; }
        public int MaxIntervalClRequestTime { get; set; } = 300;

        public float LowStorageSpaceWarningThreshold { get; set; } = 5;
        public float LowStorageSpaceShutdownThreshold { get; set; } = 1;
        public bool LowStorageCheckAwaitOnStartup { get; set; } = false;
    }
}
