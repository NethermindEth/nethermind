using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.State.Snap
{
    public class AccountsAndProofs
    {
        public IReadOnlyList<PathWithAccount> PathAndAccounts { get; set; }
        public byte[][] Proofs { get; set; }
    }
}
