// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Initializers
{
    public class NullNdmCapabilityConnector : INdmCapabilityConnector
    {
        private NullNdmCapabilityConnector()
        {
        }

        public static INdmCapabilityConnector Instance { get; } = new NullNdmCapabilityConnector();

        public void Init()
        {
        }

        public void AddCapability()
        {
        }

        public bool CapabilityAdded => false;
    }
}
