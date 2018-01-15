using System;

namespace Nevermind.Utils
{
    public static class StringExtensions
    {
        public static bool CompareIgnoreCaseTrim(this string value1, string value2)
        {
            if (string.IsNullOrEmpty(value1) && string.IsNullOrEmpty(value2))
            {
                return true;
            }
            if (string.IsNullOrEmpty(value1) || string.IsNullOrEmpty(value2))
            {
                return false;
            }
            return string.Compare(value1.Trim(), value2.Trim(), StringComparison.CurrentCultureIgnoreCase) == 0;
        }
    }
}
