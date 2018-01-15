using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Unity;

namespace Nevermind.Runner
{
    class Program
    {
        static void Main(string[] args)
        {
            var bootstraper = new Bootstraper();
            var jsonRpcRunner = bootstraper.Container.Resolve<IJsonRpcRunner>();
            jsonRpcRunner.Container = bootstraper.Container;
            jsonRpcRunner.Start();

            var ethereumRunner = bootstraper.Container.Resolve<IEthereumRunner>();
        }
    }
}
