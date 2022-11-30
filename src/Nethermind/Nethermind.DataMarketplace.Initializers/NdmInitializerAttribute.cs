// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.DataMarketplace.Initializers
{
    [AttributeUsage(AttributeTargets.Class)]
    public class NdmInitializerAttribute : Attribute
    {
        public string Name { get; }

        public NdmInitializerAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("NDM initializer name cannot be empty.", nameof(name));
            }

            Name = name;
        }
    }
}
