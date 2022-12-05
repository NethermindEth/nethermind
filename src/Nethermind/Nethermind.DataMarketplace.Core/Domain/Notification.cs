// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class Notification
    {
        public string? Type { get; set; }
        public string? Client { get; set; }
        public object? Data { get; set; }

        public Notification()
        {
        }

        public Notification(string type, object data, string client = "")
        {
            Type = type;
            Client = client;
            Data = data;
        }
    }
}
