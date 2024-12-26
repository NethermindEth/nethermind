// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.Config;
using Nethermind.Evm.Tracing;
using Nethermind.State;
using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Evm.CodeAnalysis.IL.CompilerModes.FullAOT;
internal static class FullAOR
{
    public delegate bool MoveNextDelegate(int chainId, ref int gasAvailable, ref int programCounter, ref int stackHead, ref Word stackHeadRef); // it returns true if current staet is HALTED or FINISHED and Sets Current.CallResult in case of CALL or CREATE

    
    public static void CompileContract(ContractMetadata contractMetadata, IVMConfig vmConfig)
    {
        var assemblyBuilder = new PersistedAssemblyBuilder(new AssemblyName("Nethermind.Evm.Precompiled.Live"), typeof(object).Assembly);
        ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("ContractsModule");
        TypeBuilder contractStructBuilder = moduleBuilder.DefineType($"{contractMetadata.TargetCodeInfo.Address}", TypeAttributes.Public |
            TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));

        FullAotEnvLoader envLoader = new FullAotEnvLoader(contractStructBuilder);

        EmitMoveNext(contractStructBuilder, contractMetadata, envLoader, vmConfig);

    }

    public static void EmitMoveNext(TypeBuilder contractBuilder, ContractMetadata metadata, FullAotEnvLoader envLoader, IVMConfig config)
    {
        var method = Emit<MoveNextDelegate>.BuildInstanceMethod(
            contractBuilder,
            "MoveNext",
            MethodAttributes.Public | MethodAttributes.Virtual);
    }
}

