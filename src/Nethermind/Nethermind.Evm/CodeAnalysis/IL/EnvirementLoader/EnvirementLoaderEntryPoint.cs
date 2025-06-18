// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Sigil;
using System;
using System.Linq;
using System.Reflection;
using Nethermind.Core;

using CallData = System.ReadOnlyMemory<byte>;

namespace Nethermind.Evm.CodeAnalysis.IL;
internal class EnvirementLoaderEntryPoint : IEnvirementLoader
{
    public static readonly EnvirementLoaderEntryPoint Instance = new();

    private static readonly FieldInfo FieldInputData = typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.InputData), BindingFlags.Public | BindingFlags.Instance);

    public const int REF_MACHINECODE_INDEX = 0;
    public const int OBJ_SPEC = REF_MACHINECODE_INDEX + 1;
    public const int OBJ_SPECPROVIDER_INDEX = OBJ_SPEC + 1;
    public const int OBJ_BLOCKHASHPROVIDER_INDEX = OBJ_SPECPROVIDER_INDEX + 1;
    public const int OBJ_CODEINFOPROVIDER_INDEX = OBJ_BLOCKHASHPROVIDER_INDEX + 1;
    public const int REF_EVMSTATE_INDEX = OBJ_CODEINFOPROVIDER_INDEX + 1;
    public const int OBJ_WORLDSTATE_INDEX = REF_EVMSTATE_INDEX + 1;
    public const int OBJ_RETURNDATABUFFER_INDEX = OBJ_WORLDSTATE_INDEX + 1;
    public const int REF_GASAVAILABLE_INDEX = OBJ_RETURNDATABUFFER_INDEX + 1;
    public const int REF_PROGRAMCOUNTER_INDEX = REF_GASAVAILABLE_INDEX + 1;
    public const int REF_STACKHEAD_INDEX = REF_PROGRAMCOUNTER_INDEX + 1;
    public const int REF_STACKHEADREF_INDEX = REF_STACKHEAD_INDEX + 1;
    public const int OBJ_TXTRACER_INDEX = REF_STACKHEADREF_INDEX + 1;
    public const int OBJ_LOGGER_INDEX = OBJ_TXTRACER_INDEX + 1;
    public const int REF_CURRENT_STATE = OBJ_LOGGER_INDEX + 1;

    public void LoadBlockContext<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadTxContext(il, locals, true);
        il.LoadFieldAddress(typeof(TxExecutionContext).GetField(nameof(TxExecutionContext.BlockExecutionContext)));
        if (!loadAddress)
        {
            il.LoadObject<BlockExecutionContext>();
        }
    }

    public void LoadBlockhashProvider<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_BLOCKHASHPROVIDER_INDEX);
        else
            il.LoadArgument(OBJ_BLOCKHASHPROVIDER_INDEX);
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
        il.LoadArgument(OBJ_SPECPROVIDER_INDEX);
        il.CallVirtual(typeof(ISpecProvider).GetProperty(nameof(ISpecProvider.ChainId), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());

        if (loadAddress)
        {
            il.StoreLocal(locals.uint64A);
            il.LoadLocalAddress(locals.uint64A);
        }
    }

    public void LoadCodeInfoRepository<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_CODEINFOPROVIDER_INDEX);
        else
            il.LoadArgument(OBJ_CODEINFOPROVIDER_INDEX);
    }

    public void LoadCurrStackHead<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_STACKHEADREF_INDEX);
        if (!loadAddress)
            il.LoadObject<Word>();
    }

    public void LoadEnv<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadVmState(il, locals, false);
        il.Call(typeof(EvmState).GetProperty(nameof(EvmState.Env), BindingFlags.Public | BindingFlags.Instance)!
            .GetMethod);
        if(!loadAddress)
        {
            il.LoadObject<ExecutionEnvironment>();
        }
    }

    public void LoadGasAvailable<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_GASAVAILABLE_INDEX);
        if (!loadAddress)
            il.LoadObject<long>();
    }

    public void LoadLogger<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_LOGGER_INDEX);
        else
            il.LoadArgument(OBJ_LOGGER_INDEX);
    }

    public void LoadMachineCode<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
        {
            il.LoadArgument(REF_MACHINECODE_INDEX);
        }
        else
        {
            throw new NotImplementedException("LoadMachineCode without address is not implemented");
        }
    }

    public void LoadMemory<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadVmState(il, locals, false);
        il.Call(typeof(EvmState).GetProperty(nameof(EvmState.Memory), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());
        if (!loadAddress)
            il.LoadObject<EvmPooledMemory>();
    }

    public void LoadProgramCounter<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_PROGRAMCOUNTER_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public void LoadResult<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_CURRENT_STATE);
        if (!loadAddress)
            il.LoadObject<ILChunkExecutionState>();
    }

    public void LoadSpec<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
        {
            il.LoadArgumentAddress(OBJ_SPEC);
        }
        else
        {
            il.LoadArgument(OBJ_SPEC);
        }
    }

    public void LoadStackHead<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_STACKHEAD_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public void LoadTxContext<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadEnv(il, locals, true);
        il.LoadFieldAddress(typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.TxExecutionContext), BindingFlags.Public | BindingFlags.Instance));
        if (!loadAddress)
        {
            il.LoadObject<TxExecutionContext>();
        }
    }

    public void LoadTxTracer<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_TXTRACER_INDEX);
        else
            il.LoadArgument(OBJ_TXTRACER_INDEX);
    }

    public void LoadVmState<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
        {
            il.LoadArgumentAddress(REF_EVMSTATE_INDEX);
        }
        else
        {
            il.LoadArgument(REF_EVMSTATE_INDEX);
        }
    }

    public void LoadWorldState<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_WORLDSTATE_INDEX);
        else
            il.LoadArgument(OBJ_WORLDSTATE_INDEX);
    }

    public void LoadReturnDataBuffer<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
        {
            il.LoadArgumentAddress(OBJ_RETURNDATABUFFER_INDEX);
        }
        else
        {
            il.LoadArgument(OBJ_RETURNDATABUFFER_INDEX);
        }
    }

    public void LoadHeader<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadBlockContext(il, locals, true);
        il.Call(typeof(BlockExecutionContext).GetProperty(nameof(BlockExecutionContext.Header), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());
    }


    public void LoadSpecProvider<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
        {
            il.LoadArgumentAddress(OBJ_SPECPROVIDER_INDEX);
        }
        else
        {
            il.LoadArgument(OBJ_SPECPROVIDER_INDEX);
        }
    }
}
