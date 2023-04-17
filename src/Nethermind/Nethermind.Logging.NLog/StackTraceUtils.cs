// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Nethermind.Logging
{
    // Decompiled with JetBrains decompiler
    // Type: NLog.Internal.StackTraceUsageUtils
    // Assembly: NLog, Version=4.0.0.0, Culture=neutral, PublicKeyToken=5120e14c03d0593c
    // MVID: A2EA6CB6-1E12-422B-8404-0D46451CF9D2
    /// <summary>
    /// Utilities for dealing with <see cref="T:NLog.Config.StackTraceUsage" /> values.
    /// Copied from NLog internals to skip one more class
    /// </summary>
    internal static class StackTraceUsageUtils
    {
        private static readonly Assembly NLogAssembly = typeof(StackTraceUsageUtils).Assembly;
        private static readonly Assembly MscorlibAssembly = typeof(string).Assembly;
        private static readonly Assembly SystemAssembly = typeof(Debug).Assembly;

        private static string GetStackFrameMethodClassName(MethodBase method, bool includeNameSpace, bool cleanAsyncMoveNext, bool cleanAnonymousDelegates)
        {
            if (method is null)
            {
                return null;
            }

            Type declaringType = method.DeclaringType;
            if (cleanAsyncMoveNext && method.Name == "MoveNext" && ((object)declaringType is not null ? declaringType.DeclaringType : null) is not null && declaringType.Name.StartsWith("<") && declaringType.Name.IndexOf('>', 1) > 1)
                declaringType = declaringType.DeclaringType;
            string str = includeNameSpace ? ((object)declaringType is not null ? declaringType.FullName : (string)null) : ((object)declaringType is not null ? declaringType.Name : (string)null);
            if (cleanAnonymousDelegates && str is not null)
            {
                int length = str.IndexOf("+<>", StringComparison.Ordinal);
                if (length >= 0)
                    str = str.Substring(0, length);
            }

            return str;
        }

        /// <summary>
        /// Gets the fully qualified name of the class invoking the calling method, including the
        /// namespace but not the assembly.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string GetClassFullName()
        {
            return GetClassFullName(new StackFrame(3, false));
        }

        /// <summary>
        /// Gets the fully qualified name of the class invoking the calling method, including the
        /// namespace but not the assembly.
        /// </summary>
        /// <param name="stackFrame">StackFrame from the calling method</param>
        /// <returns>Fully qualified class name</returns>
        private static string GetClassFullName(StackFrame stackFrame)
        {
            string str = LookupClassNameFromStackFrame(stackFrame);
            if (string.IsNullOrEmpty(str))
                str = GetClassFullName(new StackTrace(false));
            return str;
        }

        private static string GetClassFullName(StackTrace stackTrace)
        {
            foreach (StackFrame frame in stackTrace.GetFrames())
            {
                string str = LookupClassNameFromStackFrame(frame);
                if (!string.IsNullOrEmpty(str))
                {
                    return str;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Returns the assembly from the provided StackFrame (If not internal assembly)
        /// </summary>
        /// <returns>Valid assembly, or null if assembly was internal</returns>
        private static Assembly LookupAssemblyFromStackFrame(StackFrame stackFrame)
        {
            MethodBase method = stackFrame.GetMethod();
            if (method is null)
                return null;
            Type declaringType = method.DeclaringType;
            Assembly assembly1 = (object)declaringType is not null ? declaringType.Assembly : null;
            if ((object)assembly1 is null)
            {
                Module module = method.Module;
                assembly1 = (object)module is not null ? module.Assembly : null;
            }

            Assembly assembly2 = assembly1;
            if (assembly2 == NLogAssembly)
            {
                return null;
            }

            if (assembly2 == MscorlibAssembly)
            {
                return null;
            }

            if (assembly2 == SystemAssembly)
            {
                return null;
            }

            return assembly2;
        }

        /// <summary>
        /// Returns the classname from the provided StackFrame (If not from internal assembly)
        /// </summary>
        /// <param name="stackFrame"></param>
        /// <returns>Valid class name, or empty string if assembly was internal</returns>
        private static string LookupClassNameFromStackFrame(StackFrame stackFrame)
        {
            MethodBase method = stackFrame.GetMethod();
            if (method is not null && LookupAssemblyFromStackFrame(stackFrame) is not null)
            {
                string str = GetStackFrameMethodClassName(method, true, true, true) ?? method.Name;
                if (!string.IsNullOrEmpty(str) && !str.StartsWith("System.", StringComparison.Ordinal))
                    return str;
            }

            return string.Empty;
        }
    }
}
