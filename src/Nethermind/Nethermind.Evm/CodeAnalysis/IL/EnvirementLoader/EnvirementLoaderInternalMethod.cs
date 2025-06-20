// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
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
using System.Threading;
using Nethermind.Core;

using CallData = System.ReadOnlyMemory<byte>;

namespace Nethermind.Evm.CodeAnalysis.IL;

internal class EnvirementLoaderInternalMethod : IEnvirementLoader
{
    public static readonly EnvirementLoaderInternalMethod Instance = new();

    public const int REF_MACHINECODE_INDEX = 0;
    public const int OBJ_SPEC_INDEX = REF_MACHINECODE_INDEX + 1;
    public const int OBJ_SPECPROVIDER_INDEX = OBJ_SPEC_INDEX + 1;
    public const int OBJ_BLOCKHASHPROVIDER_INDEX = OBJ_SPECPROVIDER_INDEX + 1;
    public const int OBJ_CODEINFOPROVIDER_INDEX = OBJ_BLOCKHASHPROVIDER_INDEX + 1;
    public const int OBJ_WORLDSTATE_INDEX = OBJ_CODEINFOPROVIDER_INDEX + 1;
    public const int OBJ_EVMSTATE_INDEX = OBJ_WORLDSTATE_INDEX + 1;
    public const int REF_ENVIRONMENT_INDEX = OBJ_EVMSTATE_INDEX + 1;
    public const int REF_TXEXECUTIONCONTEXT_INDEX = REF_ENVIRONMENT_INDEX+ 1;
    public const int REF_BLOCKEXECUTIONCONTEXT_INDEX = REF_TXEXECUTIONCONTEXT_INDEX + 1;
    public const int OBJ_RETURNDATABUFFER_INDEX = REF_BLOCKEXECUTIONCONTEXT_INDEX + 1;
    public const int REF_GASAVAILABLE_INDEX = OBJ_RETURNDATABUFFER_INDEX + 1;
    public const int REF_PROGRAMCOUNTER_INDEX = REF_GASAVAILABLE_INDEX + 1;
    public const int REF_STACKHEAD_INDEX = REF_PROGRAMCOUNTER_INDEX + 1;
    public const int REF_STACKHEADREF_INDEX = REF_STACKHEAD_INDEX + 1;
    public const int OBJ_TXTRACER_INDEX = REF_STACKHEADREF_INDEX + 1;
    public const int OBJ_LOGGER_INDEX = OBJ_TXTRACER_INDEX + 1;
    public const int REF_RESULT_STATE_INDEX = OBJ_LOGGER_INDEX + 1;


    public void LoadBlockContext<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BLOCKEXECUTIONCONTEXT_INDEX);
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

    public void CacheCalldata<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals)
    {
        const string calldata = nameof(CallData);

        LoadCalldata(il, locals, false);
        locals.TryDeclareLocal(calldata, typeof(CallData));
        locals.TryStoreLocal(calldata);
    }

    public void LoadCalldata<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        const string calldata = nameof(CallData);

        if (locals.TryLoadLocal(calldata, loadAddress))
        {
            return;
        }

        LoadEnv(il, locals, false);
        il.LoadField(typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.InputData), BindingFlags.Public | BindingFlags.Instance));
        if (loadAddress)
        {
            using Local local = il.DeclareLocal<CallData>(locals.GetLocalName());
            il.StoreLocal(local);
            il.LoadLocalAddress(local);
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
            il.LoadObject<UIntPtr>();
    }

    public void LoadEnv<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        il.LoadArgument(REF_ENVIRONMENT_INDEX);
        if (!loadAddress)
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
        il.LoadArgument(REF_RESULT_STATE_INDEX);
        if (!loadAddress)
            il.LoadObject<ILChunkExecutionState>();
    }

    public void LoadSpec<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress)
    {
        if (loadAddress)
        {
            il.LoadArgumentAddress(OBJ_SPEC_INDEX);
        }
        else
        {
            il.LoadArgument(OBJ_SPEC_INDEX);
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
        il.LoadArgument(REF_TXEXECUTIONCONTEXT_INDEX);
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
            il.LoadArgumentAddress(OBJ_EVMSTATE_INDEX);
        }
        else
        {
            il.LoadArgument(OBJ_EVMSTATE_INDEX);
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
