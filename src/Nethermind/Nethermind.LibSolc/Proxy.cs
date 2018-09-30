/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

/*
** Aims of solc integration **
- Allow Nethermind to receive a Solidity code on JSON RPC, 
- Then use solc library to compile it into bytecode 
- And then deploy on the chain
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Nethermind.LibSolc.DataModel;
using Newtonsoft.Json.Linq;

namespace Nethermind.LibSolc
{
    public static class Proxy
    {
        private static readonly OsPlatform Platform;
        private static readonly ReadFileCallback Callback;

        private enum OsPlatform
        {
            Windows,
            Linux,
            Mac
        }
        
        private delegate void ReadFileCallback(string _path, ref string o_contents, ref string o_error);
        /* Looks for the solidity file in the given filepath and uses the contents reference to
         place the file contents there. In case of an error, this is reflected in the error reference
         *NOT YET FULLY IMPLEMENTED*
        */
        //TODO: maybe support both local file compilation and JSON RPC
        private static void CallBack(string path, ref string contents, ref string error)
        {
            //check path for file. IF there, read contents into &contents ELSE place 'FOF' error in &error 
            try
            {
                string filePath = path;
                
                filePath = filePath.Replace('\\', '/');
//                if (_fileContents.TryGetValue(filePath, out contents)) return;
//                if (File.Exists(filePath))
//                {
//                    _lastSourceDir = Path.GetDirectoryName(sourceFilePath);
//                    contents = File.ReadAllText(sourceFilePath, Encoding.UTF8);
//                    contents = contents.Replace("\r\n", "\n");
//                    _fileContents.Add(sourceFilePath, contents);
//                }
//                else
//                {
//                    error = "Source file not found: " + path;
//                }
            }
            catch (Exception e)
            {
                error = e.ToString();
            }
        }    

        static Proxy()
        {
            Platform = GetPlatform();
            Callback = CallBack;
        }

        private static OsPlatform GetPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) 
            {
                return OsPlatform.Windows;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) 
            {
                return OsPlatform.Linux;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) 
            {
                return OsPlatform.Mac;
            }

            throw new InvalidOperationException("Unsupported platform.");
        }
        
        private static class Win64Lib
        {
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc.dll")]
            public static extern string license();
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc.dll")]
            public static extern string version();
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc.dll")]
            public static extern string compileJSON(string _input, bool _optimize);
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc.dll")]
            public static extern string compileJSONMulti(string _input, bool _optimize);
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc.dll")]
            public static extern string compileJSONCallback(string _input, bool _optimize, ReadFileCallback _readCallback);
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc.dll")]
            public static extern string compileStandard(string _input, ReadFileCallback _readCallback);
            
//            [SuppressUnmanagedCodeSecurity]
//            [DllImport("solc.dll")]
//            public static extern string solidity_license();
//            
//            [SuppressUnmanagedCodeSecurity]
//            [DllImport("solc.dll")]
//            public static extern string solidity_version();
//            
//            [SuppressUnmanagedCodeSecurity]
//            [DllImport("solc.dll")]
//            public static extern string solidity_compile();
        }
        
        private static class PosixLib
        {
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc.so")]
            public static extern string license();
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc.so")]
            public static extern string version();
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc.so")]
            public static extern string compileJSON(string _input, bool _optimize);
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc.so")]
            public static extern string compileJSONMulti(string _input, bool _optimize);
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc.so")]
            public static extern string compileJSONCallback(string _input, bool _optimize, ReadFileCallback _readCallback);
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc.so")]
            public static extern string compileStandard(string _input, ReadFileCallback _readCallback);
        }

        private static class MacLib
        {
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc")]
            public static extern string license();
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc")]
            public static extern string version();
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc")]
            public static extern string compileJSON(string _input, bool _optimize);
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc")]
            public static extern string compileJSONMulti(string _input, bool _optimize);
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc")]
            public static extern string compileJSONCallback(string _input, bool _optimize, ReadFileCallback _readCallback);
            
            [SuppressUnmanagedCodeSecurity]
            [DllImport("solc")]
            public static extern string compileStandard(string _input, ReadFileCallback _readCallback);
        }

        public static string GetSolcLicense()
            => For(() => Win64Lib.license(),
                () => PosixLib.license(),
                () => MacLib.license());

        public static string GetSolcVersion()
            => For(() => Win64Lib.version(),
                () => PosixLib.version(),
                () => MacLib.version());

        public static string Compile(string contract, string evmVersion, bool optimize, uint? runs)
        {
            string input = new CompilerInput(contract, evmVersion, optimize, runs).Value();

            return For(() => Win64Lib.compileStandard(input, null),
                () => PosixLib.compileStandard(input, null),
                () => MacLib.compileStandard(input, null));
        }

        private static T For<T>(Func<T> windows, Func<T> linux, Func<T> mac)
        {
            switch (Platform)
            {
                case OsPlatform.Windows: return windows();
                case OsPlatform.Linux: return linux();
                case OsPlatform.Mac: return mac();
            }

            throw new InvalidOperationException("Unsupported platform.");
        }
    }
}