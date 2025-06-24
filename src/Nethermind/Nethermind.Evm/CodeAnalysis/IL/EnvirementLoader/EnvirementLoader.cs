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

namespace Nethermind.Evm.CodeAnalysis.IL;
public class EnvirementLoader 
{
    public static readonly EnvirementLoader Instance = new();

    private static readonly FieldInfo FieldInputData = typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.InputData), BindingFlags.Public | BindingFlags.Instance);

    public static readonly FieldInfo REF_MACHINECODE_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.MachineCode), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_SPEC = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.Spec), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_SPECPROVIDER_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.SpecProvider), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_BLOCKHASHPROVIDER_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.BlockhashProvider), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_CODEINFOPROVIDER_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.CodeInfoRepository), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_EVMSTATE_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.EvmState), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_WORLDSTATE_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.WorldState), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_RETURNDATABUFFER_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.ReturnDataBuffer), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_GASAVAILABLE_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.GasAvailable), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_PROGRAMCOUNTER_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.ProgramCounter), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_STACKHEAD_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.StackHead), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_STACKHEADREF_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.StackHeadRef), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_TX_CONTEXT_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.TxExecutionContext), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_BLK_CONTEXT_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.BlockExecutionContext), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_ENV_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.Environment), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo REF_MEMORY_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.Memory), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_TXTRACER_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.TxTracer), BindingFlags.Public | BindingFlags.Instance);
    public static readonly FieldInfo OBJ_LOGGER_FIELD = typeof(ILChunkExecutionArguments).GetField(nameof(ILChunkExecutionArguments.Logger), BindingFlags.Public | BindingFlags.Instance);

    public const int REF_BUNDLED_ARGS_INDEX = 0;
    public const int REF_CURRENT_STATE_INDEX = REF_BUNDLED_ARGS_INDEX + 1;

    private static void LoadRefField<TDelegate>(Emit<TDelegate> il, FieldInfo field)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        il.LoadField(field);
    }

    public void LoadBlockContext<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadRefField(il, REF_BLK_CONTEXT_FIELD);
        if (!loadAddress)
        {
            il.LoadObject<BlockExecutionContext>();
        }
    }

    public void LoadBlockhashProvider<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        if (loadAddress)
        {
            il.LoadFieldAddress(OBJ_BLOCKHASHPROVIDER_FIELD);
        }
        else
        {
            il.LoadField(OBJ_BLOCKHASHPROVIDER_FIELD);
        }
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

    public void LoadChainId<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadSpecProvider(il, locals, false);
        il.CallVirtual(typeof(ISpecProvider).GetProperty(nameof(ISpecProvider.ChainId), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());

        if (loadAddress)
        {
            il.StoreLocal(locals.uint64A);
            il.LoadLocalAddress(locals.uint64A);
        }
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

    public void LoadSpec<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        if (loadAddress)
        {
            il.LoadFieldAddress(OBJ_SPEC);
        }
        else
        {
            il.LoadField(OBJ_SPEC);
        }
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
        LoadRefField(il, REF_TX_CONTEXT_FIELD);
        if (!loadAddress)
        {
            il.LoadObject<TxExecutionContext>();
        }
    }

    public void LoadTxTracer<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        if (loadAddress)
        {
            il.LoadFieldAddress(OBJ_TXTRACER_FIELD);
        }
        else
        {
            il.LoadField(OBJ_TXTRACER_FIELD);
        }
    }

    public void LoadVmState<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        if(loadAddress)
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

    public void LoadHeader<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadBlockContext(il, locals, true);
        il.Call(typeof(BlockExecutionContext).GetProperty(nameof(BlockExecutionContext.Header), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());
    }


    public void LoadSpecProvider<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BUNDLED_ARGS_INDEX);
        if (loadAddress)
        {
            il.LoadFieldAddress(OBJ_SPECPROVIDER_FIELD);
        }
        else
        {
            il.LoadField(OBJ_SPECPROVIDER_FIELD);
        }
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
