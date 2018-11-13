namespace Nethermind.Core.Logging
{
// Decompiled with JetBrains decompiler
// Type: NLog.Internal.StackTraceUsageUtils
// Assembly: NLog, Version=4.0.0.0, Culture=neutral, PublicKeyToken=5120e14c03d0593c
// MVID: A2EA6CB6-1E12-422B-8404-0D46451CF9D2

    using NLog.Config;
    using System;
    using System.Diagnostics;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Utilities for dealing with <see cref="T:NLog.Config.StackTraceUsage" /> values.
    /// Copied from NLog internals to skip one more class
    /// </summary>
    internal static class StackTraceUsageUtils
    {
        private static readonly Assembly NlogAssembly = typeof(StackTraceUsageUtils).Assembly;
        private static readonly Assembly MscorlibAssembly = typeof(string).Assembly;
        private static readonly Assembly SystemAssembly = typeof(Debug).Assembly;

        internal static StackTraceUsage Max(StackTraceUsage u1, StackTraceUsage u2)
        {
            return (StackTraceUsage)Math.Max((int)u1, (int)u2);
        }

        public static int GetFrameCount(this StackTrace strackTrace)
        {
            return strackTrace.FrameCount;
        }

        public static string GetStackFrameMethodName(MethodBase method, bool includeMethodInfo, bool cleanAsyncMoveNext, bool cleanAnonymousDelegates)
        {
            if (method == (MethodBase)null)
                return (string)null;
            string str = method.Name;
            Type declaringType = method.DeclaringType;
            if (cleanAsyncMoveNext && str == "MoveNext" && (((object)declaringType != null ? declaringType.DeclaringType : (Type)null) != (Type)null && declaringType.Name.StartsWith("<")))
            {
                int num = declaringType.Name.IndexOf('>', 1);
                if (num > 1)
                    str = declaringType.Name.Substring(1, num - 1);
            }

            if (cleanAnonymousDelegates && str.StartsWith("<") && (str.Contains("__") && str.Contains(">")))
            {
                int startIndex = str.IndexOf('<') + 1;
                int num = str.IndexOf('>');
                str = str.Substring(startIndex, num - startIndex);
            }

            if (includeMethodInfo && str == method.Name)
                str = method.ToString();
            return str;
        }

        public static string GetStackFrameMethodClassName(MethodBase method, bool includeNameSpace, bool cleanAsyncMoveNext, bool cleanAnonymousDelegates)
        {
            if (method == (MethodBase)null)
                return (string)null;
            Type declaringType = method.DeclaringType;
            if (cleanAsyncMoveNext && method.Name == "MoveNext" && (((object)declaringType != null ? declaringType.DeclaringType : (Type)null) != (Type)null && declaringType.Name.StartsWith("<") && declaringType.Name.IndexOf('>', 1) > 1))
                declaringType = declaringType.DeclaringType;
            string str = includeNameSpace ? ((object)declaringType != null ? declaringType.FullName : (string)null) : ((object)declaringType != null ? declaringType.Name : (string)null);
            if (cleanAnonymousDelegates && str != null)
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
            int skipFrames = 3;
            string empty = string.Empty;
            int num = 0;
            return StackTraceUsageUtils.GetClassFullName(new StackFrame(skipFrames, num != 0));
        }

        /// <summary>
        /// Gets the fully qualified name of the class invoking the calling method, including the
        /// namespace but not the assembly.
        /// </summary>
        /// <param name="stackFrame">StackFrame from the calling method</param>
        /// <returns>Fully qualified class name</returns>
        public static string GetClassFullName(StackFrame stackFrame)
        {
            string str = StackTraceUsageUtils.LookupClassNameFromStackFrame(stackFrame);
            if (string.IsNullOrEmpty(str))
                str = StackTraceUsageUtils.GetClassFullName(new StackTrace(false));
            return str;
        }

        private static string GetClassFullName(StackTrace stackTrace)
        {
            foreach (StackFrame frame in stackTrace.GetFrames())
            {
                string str = StackTraceUsageUtils.LookupClassNameFromStackFrame(frame);
                if (!string.IsNullOrEmpty(str))
                    return str;
            }

            return string.Empty;
        }

        /// <summary>
        /// Returns the assembly from the provided StackFrame (If not internal assembly)
        /// </summary>
        /// <returns>Valid asssembly, or null if assembly was internal</returns>
        public static Assembly LookupAssemblyFromStackFrame(StackFrame stackFrame)
        {
            MethodBase method = stackFrame.GetMethod();
            if (method == (MethodBase)null)
                return (Assembly)null;
            Type declaringType = method.DeclaringType;
            Assembly assembly1 = (object)declaringType != null ? declaringType.Assembly : (Assembly)null;
            if ((object)assembly1 == null)
            {
                Module module = method.Module;
                assembly1 = (object)module != null ? module.Assembly : (Assembly)null;
            }

            Assembly assembly2 = assembly1;
            if (assembly2 == StackTraceUsageUtils.NlogAssembly)
                return (Assembly)null;
            if (assembly2 == StackTraceUsageUtils.MscorlibAssembly)
                return (Assembly)null;
            if (assembly2 == StackTraceUsageUtils.SystemAssembly)
                return (Assembly)null;
            return assembly2;
        }

        /// <summary>
        /// Returns the classname from the provided StackFrame (If not from internal assembly)
        /// </summary>
        /// <param name="stackFrame"></param>
        /// <returns>Valid class name, or empty string if assembly was internal</returns>
        public static string LookupClassNameFromStackFrame(StackFrame stackFrame)
        {
            MethodBase method = stackFrame.GetMethod();
            if (method != (MethodBase)null && StackTraceUsageUtils.LookupAssemblyFromStackFrame(stackFrame) != (Assembly)null)
            {
                string str = StackTraceUsageUtils.GetStackFrameMethodClassName(method, true, true, true) ?? method.Name;
                if (!string.IsNullOrEmpty(str) && !str.StartsWith("System.", StringComparison.Ordinal))
                    return str;
            }

            return string.Empty;
        }
    }
}