using System.IO.Hashing;
using System.Text;

namespace FastFormat;

internal static class CacheHelper
{
    public static byte[] ComputeHash(byte[] data) => XxHash128.Hash(data);

    public static byte[] ComputeHash(string text) => XxHash128.Hash(Encoding.UTF8.GetBytes(text));

    public static string GetRelativePath(string filePath, string gitRoot)
    {
        var rel = Path.GetRelativePath(gitRoot, filePath);
        return rel.Replace('\\', '/');
    }
}
