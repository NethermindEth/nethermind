//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Nethermind.Native
{
    public static class NativeLib
    {
        private static OsPlatform GetPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.OSArchitecture.ToString() == "Arm")
            {
                return OsPlatform.LinuxArm;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.OSArchitecture.ToString() == "Arm64")
            {
                return OsPlatform.LinuxArm64;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.OSArchitecture.ToString() == "Arm64")
            {
                return OsPlatform.MacArm64;
            }
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
            if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
            {
                return OsPlatform.Linux;
            }

            throw new InvalidOperationException("Unsupported platform.");
        }

        public static IntPtr ImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            OsPlatform platform = GetPlatform();
            string libPath = platform switch
            {
                OsPlatform.Linux => $"runtimes/linux-x64/native/lib{libraryName}.so",
                OsPlatform.Mac => $"runtimes/osx-x64/native/lib{libraryName}.dylib",
                OsPlatform.Windows => $"runtimes\\win-x64\\native\\{libraryName}.dll",
                OsPlatform.LinuxArm => $"runtimes/linux-arm/native/lib{libraryName}.so",
                OsPlatform.LinuxArm64 => $"runtimes/linux-arm64/native/lib{libraryName}.so",
                OsPlatform.MacArm64 => $"runtimes/osx-arm64/native/lib{libraryName}.dylib",
                _ => throw new NotSupportedException($"Platform support missing: {platform}")
            };
            
            // Console.WriteLine($"Trying to load a lib {libraryName} from {libPath} with search path {searchPath} for asembly {assembly} on platform {platform}");
            NativeLibrary.TryLoad(libPath, assembly, searchPath, out IntPtr libHandle);
            return libHandle;
        }
    }
}
