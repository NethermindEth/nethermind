// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Modules.Subscribe
{
    public struct SubscriptionType
    {
        public const string NewHeads = "newHeads";
        public const string Logs = "logs";
        public const string NewPendingTransactions = "newPendingTransactions";
        public const string DroppedPendingTransactions = "droppedPendingTransactions";
        public const string Syncing = "syncing";
    }
}
