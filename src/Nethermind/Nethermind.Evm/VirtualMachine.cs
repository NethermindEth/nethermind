using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Evm;

public sealed unsafe class VirtualMachine(
    IBlockhashProvider? blockHashProvider,
    ISpecProvider? specProvider,
    ILogManager? logManager
) : VirtualMachineBase(blockHashProvider, specProvider, logManager)
{
}
