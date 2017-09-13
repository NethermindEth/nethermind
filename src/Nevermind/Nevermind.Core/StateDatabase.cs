using System.Collections.Generic;

namespace Nevermind.Core
{
    public class StateDatabase
    {
        private Dictionary<byte[], byte[]> _state = new Dictionary<byte[], byte[]>();
    }

    public class World
    {
        public Dictionary<Address, Account> State { get; set; }
    }
}