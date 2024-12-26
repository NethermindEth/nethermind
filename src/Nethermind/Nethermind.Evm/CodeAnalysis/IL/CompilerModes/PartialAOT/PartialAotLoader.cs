// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Nethermind.Evm.CodeAnalysis.IL.CompilerModes.PartialAOT.PartialAOT;

namespace Nethermind.Evm.CodeAnalysis.IL.CompilerModes.PartialAOT;
internal class PartialAotEnvLoader : EnvLoader<ExecuteSegment>
{
    private const int CHAINID_INDEX = 0;
    private const int REF_VMSTATE_INDEX = 1;
    private const int REF_ENV_INDEX = 2;
    private const int REF_TXCTX_INDEX = 3;
    private const int REF_BLKCTX_INDEX = 4;
    private const int REF_MEMORY_INDEX = 5;
    private const int REF_CURR_STACK_HEAD_INDEX = 6;
    private const int STACK_HEAD_INDEX = 7;
    private const int BLOCKHASH_PROVIDER_INDEX = 8;
    private const int WORLD_STATE_INDEX = 9;
    private const int CODE_INFO_REPOSITORY_INDEX = 10;
    private const int SPEC_INDEX = 11;
    private const int TXTRACER_INDEX = 12;
    private const int LOGGER_INDEX = 13;
    private const int PROGRAM_COUNTER_INDEX = 14;
    private const int GAS_AVAILABLE_INDEX = 15;
    private const int REF_MACHINE_CODE_INDEX = 16;
    private const int REF_CALLDATA_INDEX = 17;
    private const int IMMEDIATES_DATA_INDEX = 18;
    private const int REF_RESULT_INDEX = 19;
    public override void LoadBlockContext(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        il.LoadArgument(REF_BLKCTX_INDEX);
        if (!loadAddress)
            il.LoadObject<BlockExecutionContext>();
    }

    public override void LoadBlockhashProvider(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(BLOCKHASH_PROVIDER_INDEX);
        else
        {
            il.LoadArgument(BLOCKHASH_PROVIDER_INDEX);
        }

    }

    public override void LoadCalldata(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        il.LoadArgument(REF_CALLDATA_INDEX);
        if (!loadAddress)
            il.LoadObject<ReadOnlyMemory<byte>>();
    }

    public override void LoadChainId(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(CHAINID_INDEX);
        else il.LoadArgument(CHAINID_INDEX);
    }

    public override void LoadCodeInfoRepository(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(CODE_INFO_REPOSITORY_INDEX);
        else
        {
            il.LoadArgument(CODE_INFO_REPOSITORY_INDEX);
        }
    }

    public override void LoadCurrStackHead(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        //il.LoadLocal(locals.stackHeadIdx);
        il.LoadArgument(REF_CURR_STACK_HEAD_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public override void LoadEnv(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        il.LoadArgument(REF_ENV_INDEX);
        if (!loadAddress)
            il.LoadObject<ExecutionEnvironment>();
    }

    public override void LoadGasAvailable(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        //il.LoadLocal(locals.gasAvailable);
        il.LoadArgument(GAS_AVAILABLE_INDEX);
        if (!loadAddress)
            il.LoadObject<long>();
    }

    public override void LoadImmediatesData(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(IMMEDIATES_DATA_INDEX);
        else
        {
            il.LoadArgument(IMMEDIATES_DATA_INDEX);
        }
    }

    public override void LoadLogger(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(LOGGER_INDEX);
        else
        {
            il.LoadArgument(LOGGER_INDEX);
        }
    }

    public override void LoadMachineCode(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        il.LoadArgument(REF_MACHINE_CODE_INDEX);
        if (!loadAddress)
            il.LoadObject<ReadOnlyMemory<byte>>();

    }

    public override void LoadMemory(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        il.LoadArgument(REF_MEMORY_INDEX);
        if (!loadAddress)
            il.LoadObject<EvmPooledMemory>();
    }

    public override void LoadProgramCounter(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        il.LoadArgument(PROGRAM_COUNTER_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }
    public override void LoadResult(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        il.LoadArgument(REF_RESULT_INDEX);
        if (!loadAddress)
            il.LoadObject(typeof(ILChunkExecutionState));
    }

    public override void LoadSpec(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(SPEC_INDEX);
        else
        {
            il.LoadArgument(SPEC_INDEX);
        }
    }

    public override void LoadStackHead(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        il.LoadArgument(STACK_HEAD_INDEX);
        if (!loadAddress)
            il.LoadObject<int>();
    }

    public void LoadStackHeadRef(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        il.LoadArgument(REF_CURR_STACK_HEAD_INDEX);
        if (!loadAddress)
            il.LoadObject<Word>();
    }

    public override void LoadTxContext(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        il.LoadArgument(REF_TXCTX_INDEX);
        if (!loadAddress)
            il.LoadObject<TxExecutionContext>();
    }

    public override void LoadTxTracer(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(TXTRACER_INDEX);
        else
        {
            il.LoadArgument(TXTRACER_INDEX);
        }
    }

    public override void LoadVmState(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        il.LoadArgument(REF_VMSTATE_INDEX);
        if (!loadAddress)
            il.LoadIndirect<EvmState>();
    }

    public override void LoadWorldState(Emit<ExecuteSegment> il, Locals<ExecuteSegment> locals, bool loadAddress)
    {
        if (loadAddress)
            il.LoadArgumentAddress(WORLD_STATE_INDEX);
        else
        {
            il.LoadArgument(WORLD_STATE_INDEX);
        }
    }
}
