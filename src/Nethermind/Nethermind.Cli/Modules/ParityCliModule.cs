using Jint.Native;

namespace Nethermind.Cli.Modules
{
    [CliModule("parity")]
    public class ParityCliModule : CliModuleBase
    {
        public ParityCliModule(ICliEngine engine, INodeManager nodeManager) : base(engine, nodeManager)
        {
        }
        
        [CliFunction("parity", "pendingTransactions", Description = "Returns the pending transactions using Parity format")]
        public JsValue ReplayTransaction()
        {
            return NodeManager.PostJint("parity_pendingTransactions").Result;
        }
    }
}