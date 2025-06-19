// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL
{
    public interface IEnvirementLoader
    {
        void LoadArguments<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadBlockContext<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadBlockhashProvider<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadCalldata<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadChainId<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadCodeInfoRepository<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadCurrStackHead<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadEnv<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadGasAvailable<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadHeader<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadLogger<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadMachineCode<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadMemory<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadProgramCounter<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadResult<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadReturnDataBuffer<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadSpec<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadSpecProvider<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadStackHead<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadTxContext<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadTxTracer<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadVmState<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
        void LoadWorldState<TDelegate>(Emit<TDelegate> il, Locals<TDelegate> locals, bool loadAddress);
    }
}
