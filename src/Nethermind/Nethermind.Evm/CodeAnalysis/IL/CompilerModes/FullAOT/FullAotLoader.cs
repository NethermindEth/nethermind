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

namespace Nethermind.Evm.CodeAnalysis.IL.CompilerModes;
internal class FullAotEnvLoader : EnvLoader<PrecompiledContract>
{

    public const int OBJ_CONTRACTMETADATA_INDEX = 0;
    public const int OBJ_SPECPROVIDER_INDEX = OBJ_CONTRACTMETADATA_INDEX + 1;
    public const int OBJ_BLOCKHASHPROVIDER_INDEX = OBJ_SPECPROVIDER_INDEX + 1;
    public const int OBJ_CODEINFOPROVIDER_INDEX = OBJ_BLOCKHASHPROVIDER_INDEX + 1;
    public const int REF_EVMSTATE_INDEX = OBJ_CODEINFOPROVIDER_INDEX + 1;
    public const int OBJ_WORLDSTATE_INDEX = REF_EVMSTATE_INDEX + 1;
    public const int REF_GASAVAILABLE_INDEX = OBJ_WORLDSTATE_INDEX + 1;
    public const int REF_PROGRAMCOUNTER_INDEX = REF_GASAVAILABLE_INDEX + 1;
    public const int REF_STACKHEAD_INDEX = REF_PROGRAMCOUNTER_INDEX + 1;
    public const int REF_STACKHEADREF_INDEX = REF_STACKHEAD_INDEX + 1;
    public const int REF_RETURNDATABUFFER_INDEX = REF_STACKHEADREF_INDEX + 1;
    public const int OBJ_TXTRACER_INDEX = REF_RETURNDATABUFFER_INDEX + 1;
    public const int OBJ_LOGGER_INDEX = OBJ_TXTRACER_INDEX + 1;
    public const int REF_CURRENT_STATE = OBJ_LOGGER_INDEX + 1;

    public Dictionary<string, FieldBuilder> Fields { get; } = new();

    public void CacheBlockContext(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals)
    {
        LoadBlockContext(il, locals, false);
        locals.TryDeclareLocal("blockContext", typeof(BlockExecutionContext));
        locals.TryStoreLocal("blockContext");
    }
    public override void LoadBlockContext(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        string blockContext = "blockContext";
        if (locals.TryLoadLocal(blockContext, loadAddress))
        {
            return;
        }

        LoadTxContext(il, locals, false);
        il.LoadField(typeof(TxExecutionContext).GetField(nameof(TxExecutionContext.BlockExecutionContext)));
        if (loadAddress)
        {
            using Local local = il.DeclareLocal<BlockExecutionContext>();
            il.StoreLocal(local);
            il.LoadLocalAddress(local);
        }
    }

    public override void LoadBlockhashProvider(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_BLOCKHASHPROVIDER_INDEX);
        else
            il.LoadArgument(OBJ_BLOCKHASHPROVIDER_INDEX);
    }

    public void CacheCalldata(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals)
    {
        LoadCalldata(il, locals, false);
        locals.TryDeclareLocal("calldata", typeof(ReadOnlyMemory<byte>));
        locals.TryStoreLocal("calldata");
    }

    public override void LoadCalldata(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        string calldata = "calldata";

        if (locals.TryLoadLocal(calldata, loadAddress))
        {
            return;
        }

        LoadEnv(il, locals, false);
        il.LoadField(typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.InputData), BindingFlags.Public | BindingFlags.Instance));
        if (loadAddress)
        {
            using Local local = il.DeclareLocal<ReadOnlyMemory<byte>>();
            il.StoreLocal(local);
            il.LoadLocalAddress(local);
        }
    }

    public override void LoadChainId(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        il.LoadArgument(OBJ_SPECPROVIDER_INDEX);
        il.CallVirtual(typeof(ISpecProvider).GetProperty(nameof(ISpecProvider.ChainId), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());

        if(loadAddress)
        {
            il.StoreLocal(locals.uint64A);
            il.LoadLocalAddress(locals.uint64A);
        }
    }

    public override void LoadCodeInfoRepository(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_CODEINFOPROVIDER_INDEX);
        else
            il.LoadArgument(OBJ_CODEINFOPROVIDER_INDEX);
    }

    public override void LoadCurrStackHead(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        il.LoadArgument(REF_STACKHEADREF_INDEX);
        if (!loadAddress)
            il.LoadObject<Word>();
    }

    public void CacheEnv(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals)
    {
        LoadEnv(il, locals, false);
        locals.TryDeclareLocal("env", typeof(ExecutionEnvironment));
        locals.TryStoreLocal("env");
    }

    public override void LoadEnv(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        string env = "env";

        if (locals.TryLoadLocal(env, loadAddress))
        {
            return;
        }

        LoadVmState(il, locals, false);
        il.Call(typeof(EvmState).GetProperty(nameof(EvmState.Env), BindingFlags.Public | BindingFlags.Instance).GetMethod);
        if(!loadAddress)
        {
            il.LoadObject<ExecutionEnvironment>();
        }
    }

    public override void LoadGasAvailable(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        il.LoadArgument(REF_GASAVAILABLE_INDEX);
        if (!loadAddress)
            il.LoadObject<long>();
    }

    public override void LoadImmediatesData(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        il.LoadArgument(OBJ_CONTRACTMETADATA_INDEX);
        il.Call(typeof(ContractMetadata).GetProperty(nameof(ContractMetadata.EmbeddedData), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());
        if(loadAddress)
        {
            using Local local = il.DeclareLocal<byte[][]>();
            il.StoreLocal(local);
            il.LoadLocalAddress(local);
        }
    }

    public override void LoadLogger(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_LOGGER_INDEX);
        else
            il.LoadArgument(OBJ_LOGGER_INDEX);
    }

    public override void LoadMachineCode(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        il.LoadArgument(OBJ_CONTRACTMETADATA_INDEX);
        il.Call(typeof(ContractMetadata).GetProperty(nameof(ContractMetadata.TargetCodeInfo), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());
        il.Call(typeof(CodeInfo).GetProperty(nameof(CodeInfo.MachineCode), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());

        if (loadAddress)
        {
            using Local local = il.DeclareLocal<ReadOnlyMemory<byte>>();
            il.StoreLocal(local);
            il.LoadLocalAddress(local);
        }
    }

    public override void LoadMemory(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        LoadVmState(il, locals, false);
        il.Call(typeof(EvmState).GetProperty(nameof(EvmState.Memory), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());
        if (!loadAddress)
            il.LoadObject<EvmPooledMemory>();
    }

    public override void LoadProgramCounter(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        il.LoadArgument(REF_PROGRAMCOUNTER_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public override void LoadResult(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        il.LoadArgument(REF_CURRENT_STATE);
        if (!loadAddress)
            il.LoadObject<ILChunkExecutionState>();
    }

    public void CacheSpec(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals)
    {
        LoadSpec(il, locals, false);
        locals.TryDeclareLocal("spec", typeof(IReleaseSpec));
        locals.TryStoreLocal("spec");
    }

    public override void LoadSpec(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        string spec = "spec";

        if (locals.TryLoadLocal(spec, loadAddress))
        {
            return;
        }

        il.LoadArgument(OBJ_SPECPROVIDER_INDEX);

        LoadHeader(il, locals, false);
        il.Call(typeof(SpecProviderExtensions).GetMethod(nameof(SpecProviderExtensions.GetSpec), [typeof(ISpecProvider), typeof(BlockHeader)]));

        if (loadAddress)
        {
            using Local local = il.DeclareLocal<IReleaseSpec>();
            il.StoreLocal(local);
            il.LoadLocalAddress(local);
        }

    }

    public override void LoadStackHead(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        il.LoadArgument(REF_STACKHEAD_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public void CacheTxContext(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals)
    {
        LoadTxContext(il, locals, false);
        locals.TryDeclareLocal("txContext", typeof(TxExecutionContext));
        locals.TryStoreLocal("txContext");
    }

    public override void LoadTxContext(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        string txContext = "txContext";

        if (locals.TryLoadLocal(txContext, loadAddress))
        {
            return;
        }

        LoadEnv(il, locals, true);
        il.LoadField(typeof(ExecutionEnvironment).GetField(nameof(ExecutionEnvironment.TxExecutionContext), BindingFlags.Public | BindingFlags.Instance));
        if (loadAddress)
        {
            using Local local = il.DeclareLocal<TxExecutionContext>();
            il.StoreLocal(local);
            il.LoadLocalAddress(local);
        }
    }

    public override void LoadTxTracer(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_TXTRACER_INDEX);
        else
            il.LoadArgument(OBJ_TXTRACER_INDEX);
    }

    public override void LoadVmState(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        if(loadAddress)
        {
            il.LoadArgumentAddress(REF_EVMSTATE_INDEX);
        }
        else
        {
            il.LoadArgument(REF_EVMSTATE_INDEX);
        }
    }

    public override void LoadWorldState(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(OBJ_WORLDSTATE_INDEX);
        else
            il.LoadArgument(OBJ_WORLDSTATE_INDEX);
    }

    public override void LoadReturnDataBuffer(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        il.LoadArgument(REF_RETURNDATABUFFER_INDEX);
        if (!loadAddress)
        {
            il.LoadObject(typeof(ReadOnlyMemory<byte>));
        }
    }

    public override void LoadHeader(Emit<PrecompiledContract> il, Locals<PrecompiledContract> locals, bool loadAddress)
    {
        LoadBlockContext(il, locals, true);
        il.Call(typeof(BlockExecutionContext).GetProperty(nameof(BlockExecutionContext.Header), BindingFlags.Public | BindingFlags.Instance).GetGetMethod());
    }
}
