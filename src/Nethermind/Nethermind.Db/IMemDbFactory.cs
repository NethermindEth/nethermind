using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Db
{
    public interface IMemDbFactory
    {
        IDb CreateDb(string dbName);
    }
}
