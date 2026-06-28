// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.Evm.GasPolicy;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class EthereumGasPolicyLayoutTests
{
    // The live consensus gas budget must stay FLAT top-level scalar fields so the JIT enregisters
    // it on the per-opcode hot path. A vector / [InlineArray] / nested-struct field address-exposes
    // the containing struct (dotnet/runtime#110968) and defeats physical promotion, regressing every
    // opcode (measured: +15-18ns across 114 opcodes). New gas dimensions (multigas) must therefore be
    // added as additional flat scalar fields, never as a vector. This guard fails fast if that breaks.
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
