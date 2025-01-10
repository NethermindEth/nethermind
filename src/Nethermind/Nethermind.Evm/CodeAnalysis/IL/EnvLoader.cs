// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL;

internal abstract class EnvLoader<T>
{
    public abstract void LoadHeader(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadChainId(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadVmState(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadEnv(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadTxContext(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadBlockContext(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadMemory(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadCurrStackHead(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadStackHead(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadBlockhashProvider(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadWorldState(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadCodeInfoRepository(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadSpec(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadTxTracer(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadProgramCounter(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadGasAvailable(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadMachineCode(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadCalldata(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadImmediatesData(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadResult(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadLogger(Emit<T> il, Locals<T> locals, bool loadAddress);
    public abstract void LoadReturnDataBuffer(Emit<T> il, Locals<T> locals, bool loadAddress);
}
