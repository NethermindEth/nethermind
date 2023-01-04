// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
