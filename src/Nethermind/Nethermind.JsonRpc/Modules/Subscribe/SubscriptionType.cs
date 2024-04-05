// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
