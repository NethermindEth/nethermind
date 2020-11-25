//  Copyright (c) 2020 Demerzel Solutions Limited
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

using Nethermind.Config;

namespace Nethermind.HealthChecks
{
    public interface IHealthChecksConfig : IConfig
    {
        [ConfigItem(Description = "If 'true' then Health Check endpoints is enabled at /health", DefaultValue = "false")]
        public bool Enabled { get; set; }

        [ConfigItem(Description = "If 'true' then Webhooks can be configured", DefaultValue = "false")]
        public bool WebhooksEnabled { get; set; }

        [ConfigItem(Description = "The Webhooks endpoints e.g. Slack WebHooks", DefaultValue = "null")]
        public string WebhooksUri { get; set; }

        [ConfigItem(Description = "Payload is the json payload that will be send on Failure and must be escaped.", DefaultValue = "null")]
        public string WebhooksPayload { get; set; }

        [ConfigItem(Description = "RestorePayload is the json payload that will be send on Recovery and must be escaped.", DefaultValue = "null")]
        public string WebhooksRestorePayload { get; set; }

        [ConfigItem(Description = "If 'true' then HealthChecks UI will be avaiable at /healthchecks-ui", DefaultValue = "false")]
        public bool UIEnabled { get; set; }

        [ConfigItem(Description = "Configures the UI to poll for healthchecks updates", DefaultValue = "5")]
        public int PollingInterval { get; set; }
    }
}
