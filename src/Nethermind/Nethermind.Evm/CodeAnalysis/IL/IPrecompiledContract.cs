// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL;

public enum IContractState
{
    NotStarting, // initial state
    Running, // running currect contract
    Halted, // halted execution to execute CALL or CREATE opcode
    Finished,  // finished execution due to RETURN or REVERT opcode or STOP
    Failed // failed execution due to EvmException
}
internal interface IPrecompiledContract
{
    internal ILChunkExecutionState Current { get; set; }
    internal IContractState State { get; set; }
    public EvmState EvmState { get; init; }
    public IWorldState WorldState { get; init; }
    public ICodeInfoRepository CodeInfoRepository { get; init; }
    public ISpecProvider SpecProvider { get; init; }
    public IBlockhashProvider BlockhashProvider { get; init; }
    internal bool MoveNext(ref int GasAvailable, ref int programCounter, ref int stackHead, ref Word stackHeadRef); // it returns true if current staet is HALTED or FINISHED and Sets Current.CallResult in case of CALL or CREATE

}
