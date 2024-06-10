// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class SimulateBlockResult : BlockHeader
{
    public SimulateBlockResult(BlockHeader source)
    {
        MemberwiseCloneInto(source, this);
    }

    public static void MemberwiseCloneInto<T>(T source, T target)
    {
        if (source == null || target == null)
            throw new ArgumentNullException("Source or/and Target are null");

        Type type = typeof(T);

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite)
            {
                var value = property.GetValue(source, null);
                property.SetValue(target, value, null);
            }
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = field.GetValue(source);
            field.SetValue(target, value);
        }
    }


    public List<SimulateCallResult> Calls { get; set; } = new();
    public UInt256 BlobBaseFee { get; set; }
}
