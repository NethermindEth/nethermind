using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Nethermind.Config;


namespace Nethermind.WriteTheDocs
{
    class Program
    {
        static void Main(string[] args)
        {
            ConfigDocsGenerator generator = new ConfigDocsGenerator();
            generator.Generate();
        }
    }
}