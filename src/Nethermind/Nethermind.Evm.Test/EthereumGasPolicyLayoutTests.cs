// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.Evm.GasPolicy;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class EthereumGasPolicyLayoutTests
{
    // The live gas budget must stay flat scalar fields so the JIT enregisters it on the hot path; a
    // vector / [InlineArray] / nested-struct field address-exposes the struct (dotnet/runtime#110968)
    // and regresses every opcode. Multigas dimensions are added as flat scalars, never a vector.
    [Test]
    public void Gas_policy_struct_contains_only_flat_scalar_fields()
    {
        FieldInfo[] fields = typeof(EthereumGasPolicy)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.That(fields, Is.Not.Empty);
        foreach (FieldInfo field in fields)
        {
            Assert.That(field.FieldType.IsPrimitive, Is.True,
                $"EthereumGasPolicy.{field.Name} is '{field.FieldType.Name}', not a flat scalar. " +
                "The gas-policy struct must hold only primitive scalar fields to stay enregistered on " +
                "the hot path; add gas dimensions as flat scalar fields, never a vector/InlineArray/nested struct.");
        }
    }
}
