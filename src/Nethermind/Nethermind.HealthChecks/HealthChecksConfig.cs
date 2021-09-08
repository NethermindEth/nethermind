//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
    }
}
