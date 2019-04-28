using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
namespace Nethermind.Blockchain.Synchronization
{
    internal interface ISyncProgressResolver
    {
        long FindBestFullState();
        
        long FindBestHeader();
        
        long FindBestFullBlock();
    }
}