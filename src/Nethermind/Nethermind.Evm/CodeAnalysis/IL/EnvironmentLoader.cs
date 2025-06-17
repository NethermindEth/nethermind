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
internal static class EnvironmentLoader
{
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

    public static void CacheBlockContext<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals)
    {
        const string blockContext = nameof(BlockExecutionContext);

        LoadBlockContext(il, locals, false);
        locals.TryDeclareLocal(blockContext, typeof(BlockExecutionContext));
        locals.TryStoreLocal(blockContext);
    }
    public static void LoadBlockContext<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        const string blockContext = nameof(BlockExecutionContext);

        if (locals.TryLoadLocal(blockContext, loadAddress))
        {
            return;
        }

        LoadTxContext(il, locals, false);
        il.LoadField(typeof(TxExecutionContext).GetField(nameof(TxExecutionContext.BlockExecutionContext)));
        if (loadAddress)
        {
            using Local local = il.DeclareLocal<BlockExecutionContext>(locals.GetLocalName());
            il.StoreLocal(local);
            il.LoadLocalAddress(local);
        }
    }

    public static void LoadBlockhashProvider<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_BLOCKHASHPROVIDER_INDEX);
        else
            il.LoadArgument(OBJ_BLOCKHASHPROVIDER_INDEX);
    }

    public static void LoadCallData<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        const string calldata = nameof(CallData);

        if (locals.TryLoadLocal(calldata, loadAddress))
        {
            return;
        }

        // ref env
        LoadEnvRef(il, locals);

        // ref InputData
        il.LoadFieldAddress(FieldInputData);

        if (loadAddress == false)
        {
            il.LoadObject<CallData>();
        }
    }

    public static void LoadChainId<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(OBJ_SPECPROVIDER_INDEX);
        il.CallVirtual(typeof(ISpecProvider).GetProperty(nameof(ISpecProvider.ChainId), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());

        if (loadAddress)
        {
            il.StoreLocal(locals.uint64A);
            il.LoadLocalAddress(locals.uint64A);
        }
    }

    public static void LoadCodeInfoRepository<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_CODEINFOPROVIDER_INDEX);
        else
            il.LoadArgument(OBJ_CODEINFOPROVIDER_INDEX);
    }

    public static void LoadCurrStackHead<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_STACKHEADREF_INDEX);
        if (!loadAddress)
            il.LoadObject<Word>();
    }

    public static void LoadEnvRef<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals)
    {
        LoadVmState(il, locals, false);
        il.Call(typeof(EvmState).GetProperty(nameof(EvmState.Env), BindingFlags.Public | BindingFlags.Instance)!
            .GetMethod);
    }

    public static void LoadGasAvailable<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_GASAVAILABLE_INDEX);
        if (!loadAddress)
            il.LoadObject<long>();
    }

    public static void LoadLogger<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_LOGGER_INDEX);
        else
            il.LoadArgument(OBJ_LOGGER_INDEX);
    }

    public static void LoadMachineCode<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
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

    public static void LoadMemory<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadVmState(il, locals, false);
        il.Call(typeof(EvmState).GetProperty(nameof(EvmState.Memory), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());
        if (!loadAddress)
            il.LoadObject<EvmPooledMemory>();
    }

    public static void LoadProgramCounter<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_PROGRAMCOUNTER_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public static void LoadResult<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_CURRENT_STATE);
        if (!loadAddress)
            il.LoadObject<ILChunkExecutionState>();
    }

    public static void LoadSpec<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if(loadAddress)
        {
            il.LoadArgumentAddress(OBJ_SPEC);
        }
        else
        {
            il.LoadArgument(OBJ_SPEC);
        }
    }

    public static void LoadStackHead<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_STACKHEAD_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public static void CacheTxContext<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals)
    {
        const string txContext = nameof(TxExecutionContext);

        LoadTxContext(il, locals, false);
        locals.TryDeclareLocal(txContext, typeof(TxExecutionContext));
        locals.TryStoreLocal(txContext);
    }

    public static void LoadTxContext<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        const string txContext = nameof(TxExecutionContext);

        if (locals.TryLoadLocal(txContext, loadAddress))
        {
            return;
        }

        LoadEnvRef(il, locals);
        il.LoadField(typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.TxExecutionContext), BindingFlags.Public | BindingFlags.Instance));
        if (loadAddress)
        {
            using Local local = il.DeclareLocal<TxExecutionContext>(locals.GetLocalName());
            il.StoreLocal(local);
            il.LoadLocalAddress(local);
        }
    }

    public static void LoadTxTracer<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_TXTRACER_INDEX);
        else
            il.LoadArgument(OBJ_TXTRACER_INDEX);
    }

    public static void LoadVmState<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
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

    public static void LoadWorldState<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_WORLDSTATE_INDEX);
        else
            il.LoadArgument(OBJ_WORLDSTATE_INDEX);
    }

    public static void LoadReturnDataBuffer<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
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

    public static void LoadHeader<TDelegate>(this Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        LoadBlockContext(il, locals, true);
        il.Call(typeof(BlockExecutionContext).GetProperty(nameof(BlockExecutionContext.Header), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());
    }
}
