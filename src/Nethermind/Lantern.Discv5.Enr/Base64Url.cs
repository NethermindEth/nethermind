using System.Text;

namespace Lantern.Discv5.Enr;

public static class Base64Url
{
    private const char Base64UrlPlusReplacement = '-';
    private const char Base64UrlSlashReplacement = '_';
    private const char Base64Plus = '+';
    private const char Base64Slash = '/';
    private const char Base64Padding = '=';
    private const int Base64PaddingLength = 4;

    public static string ToString(byte[] input)
    {
        var base64 = Convert.ToBase64String(input);
        return base64.TrimEnd(Base64Padding).Replace(Base64Plus, Base64UrlPlusReplacement).Replace(Base64Slash, Base64UrlSlashReplacement);
    }

    public static byte[] ToBytes(string input)
    {
        var base64 = input.Replace(Base64UrlPlusReplacement, Base64Plus).Replace(Base64UrlSlashReplacement, Base64Slash);
        var sb = new StringBuilder(base64);
        while (sb.Length % Base64PaddingLength != 0)
        {
            sb.Append(Base64Padding);
        }
        return Convert.FromBase64String(sb.ToString());
    }
}