// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Sigil;
using System;
using System.Linq;
using System.Reflection;
using Nethermind.Core;

using CallData = System.ReadOnlyMemory<byte>;
using Nethermind.Evm.CodeAnalysis.IL.ArgumentBundle;
using Nethermind.State;
using Nethermind.Evm.Tracing;
using Microsoft.Extensions.Logging.Abstractions;

using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;

namespace Nethermind.Evm.CodeAnalysis.IL;
public class EnvirementLoader
{
    public static readonly EnvirementLoader Instance = new();

    private static readonly FieldInfo FieldInputData = typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.InputData), BindingFlags.Public | BindingFlags.Instance);

    public static readonly FieldInfo REF_MACHINECODE_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.MachineCode), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo VM_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.Vm), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_CODEINFOPROVIDER_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.CodeInfoRepository), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_EVMSTATE_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.EvmState), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_WORLDSTATE_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.WorldState), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_RETURNDATABUFFER_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.ReturnDataBuffer), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_GASAVAILABLE_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.GasAvailable), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_PROGRAMCOUNTER_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.ProgramCounter), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_STACKHEAD_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.StackHead), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_STACKHEADREF_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.StackHeadRef), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_ENV_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.Environment), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_MEMORY_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.Memory), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_LOGGER_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.Logger), BindingFlags.Public | BindingFlags.Instance);

    private static readonly MethodInfo VmSpecPropGet = typeof(VirtualMachine).GetProperty(nameof(VirtualMachine.Spec)).GetGetMethod(false);
    private static readonly MethodInfo VmTxExecContextPropGet = typeof(VirtualMachine).GetProperty(nameof(VirtualMachine.TxExecutionContext)).GetGetMethod(false);
    private static readonly MethodInfo VmBlkExecContextPropGet = typeof(VirtualMachine).GetProperty(nameof(VirtualMachine.BlockExecutionContext)).GetGetMethod(false);
    private static readonly MethodInfo VmTxTracerPropGet = typeof(VirtualMachine).GetProperty(nameof(VirtualMachine.TxTracer)).GetGetMethod(false);
    private static readonly MethodInfo VmBlockHashProviderPropGet = typeof(VirtualMachine).GetProperty(nameof(VirtualMachine.BlockHashProvider)).GetGetMethod(false);
    private static readonly MethodInfo VmChainIdPropGet = typeof(VirtualMachine).GetProperty(nameof(VirtualMachine.ChainId)).GetGetMethod(false);

    public const int REF_BUNDLED_ARGS_INDEX = 0;
    public const int REF_CURRENT_STATE_INDEX = REF_BUNDLED_ARGS_INDEX + 1;

    private static void LoadRefField<TDelegate>(Emit<TDelegate> il, FieldInfo field)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        il.LoadField(field);
    }

    public void LoadBlockContext<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        il.LoadField(VM_FIELD);

        // ref returning property
        il.Call(VmBlkExecContextPropGet);

        if (!loadAddress)
        {
            il.LoadObject<BlockExecutionContext>();
        }
    }

    public void LoadBlockhashProvider<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        il.LoadField(VM_FIELD);
        il.Call(VmBlockHashProviderPropGet);
    }

    public void LoadCalldata<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadEnv(il, locals, true);
        il.LoadFieldAddress(FieldInputData);

        if (!loadAddress)
        {
            il.LoadObject<CallData>();
        }
    }

    public void LoadChainId<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        il.LoadField(VM_FIELD);
        // ref returning property
        il.Call(VmChainIdPropGet);
    }

    public void LoadCodeInfoRepository<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        if (loadAddress)
        {
            il.LoadFieldAddress(OBJ_CODEINFOPROVIDER_FIELD);
        }
        else
        {
            il.LoadField(OBJ_CODEINFOPROVIDER_FIELD);
        }
    }

    public void LoadCurrStackHead<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadRefField(il, REF_STACKHEADREF_FIELD);
        if (!loadAddress)
        {
            throw new NotImplementedException("LoadCurrStackHead without address is not implemented");
        }
    }

    public void LoadEnv<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadRefField(il, REF_ENV_FIELD);
        if (!loadAddress)
        {
            il.LoadObject<ExecutionEnvironment>();
        }
    }

    public void LoadGasAvailable<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadRefField(il, REF_GASAVAILABLE_FIELD);
        if (!loadAddress)
        {
            il.LoadObject<ulong>();
        }
    }

    public void LoadLogger<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        if (loadAddress)
        {
            il.LoadFieldAddress(OBJ_LOGGER_FIELD);
        }
        else
        {
            il.LoadField(OBJ_LOGGER_FIELD);
        }
    }

    public void LoadMachineCode<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
        {
            LoadRefField(il, REF_MACHINECODE_FIELD);
        }
        else
        {
            throw new NotImplementedException("LoadMachineCode without address is not implemented");
        }
    }

    public void LoadMemory<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadRefField(il, REF_MEMORY_FIELD);
        if (!loadAddress)
        {
            il.LoadIndirect<Memory<byte>>();
        }
    }

    public void LoadProgramCounter<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadRefField(il, REF_PROGRAMCOUNTER_FIELD);
        if (!loadAddress)
        {
            il.LoadObject<int>();
        }
    }

    public void LoadResult<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_CURRENT_STATE_INDEX);
        if (!loadAddress)
            il.LoadObject<ILChunkExecutionState>();
    }

    public void LoadSpec<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        il.LoadField(VM_FIELD);
        il.Call(VmSpecPropGet);
    }

    public void LoadStackHead<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadRefField(il, REF_STACKHEAD_FIELD);
        if (!loadAddress)
        {
            il.LoadObject<int>();
        }
    }

    public void LoadTxContext<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        il.LoadField(VM_FIELD);

        // ref returning property
        il.Call(VmTxExecContextPropGet);

        if (!loadAddress)
        {
            il.LoadObject<TxExecutionContext>();
        }
    }

    public void LoadTxTracer<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        il.LoadField(VM_FIELD);
        il.Call(VmTxTracerPropGet);
    }

    public void LoadVmState<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        if (loadAddress)
        {
            il.LoadFieldAddress(OBJ_EVMSTATE_FIELD);
        }
        else
        {
            il.LoadField(OBJ_EVMSTATE_FIELD);
        }
    }

    public void LoadWorldState<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        if (loadAddress)
        {
            il.LoadFieldAddress(OBJ_WORLDSTATE_FIELD);
        }
        else
        {
            il.LoadField(OBJ_WORLDSTATE_FIELD);
        }
    }

    public void LoadReturnDataBuffer<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        if (loadAddress)
        {
            il.LoadFieldAddress(OBJ_RETURNDATABUFFER_FIELD);
        }
        else
        {
            il.LoadField(OBJ_RETURNDATABUFFER_FIELD);
        }
    }

    public void LoadHeaderFieldByRef<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, string fieldName, BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.Instance)
    {
        LoadHeader(il, locals);
        il.LoadFieldAddress(GetFieldInfo(typeof(BlockHeader), fieldName, bindingFlags));
    }

    public void LoadHeader<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals)
    {
        LoadBlockContext(il, locals, true);
        il.LoadField(typeof(BlockExecutionContext).GetField(nameof(BlockExecutionContext.Header), BindingFlags.Public | BindingFlags.Instance));
    }

    public void LoadArguments<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        if (!loadAddress)
        {
            il.LoadObject(typeof(ILChunkExecutionArguments));
        }
    }
}
