// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Abi
{
    [AttributeUsage(AttributeTargets.Property)]
    public class AbiTypeMappingAttribute(Type abiType, params object[] args) : Attribute
    {
        public AbiType AbiType { get; } = (AbiType)Activator.CreateInstance(abiType, args)! ?? throw new ArgumentException($"Cannot create type {abiType}", nameof(abiType));
    }
}
