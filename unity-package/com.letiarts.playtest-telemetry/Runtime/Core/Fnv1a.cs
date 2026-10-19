using System.Text;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// FNV-1a 64-bit. string.GetHashCode() is NOT used anywhere: it differs between runs and platforms.
    /// </summary>
    internal static class Fnv1a
    {
        public const ulong OffsetBasis = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        public static ulong HashBytes(byte[] data, ulong seed = OffsetBasis)
        {
            ulong h = seed;
            for (int i = 0; i < data.Length; i++)
            {
                h ^= data[i];
                h *= Prime;
            }
            return h;
        }

        /// <summary>Hash of the UTF-8 encoding of the string. Stable across platforms.</summary>
        public static ulong HashUtf8(string s)
        {
            return HashBytes(Encoding.UTF8.GetBytes(s ?? ""));
        }

        /// <summary>
        /// Allocation-free hash over UTF-16 code units (low byte, then high byte). Stable, but a different
        /// value than <see cref="HashUtf8"/>; used for in-memory cache keys only.
        /// </summary>
        public static ulong HashChars(string s, ulong seed = OffsetBasis)
        {
            ulong h = seed;
            if (s == null) return h;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                h ^= (byte)(c & 0xFF);
                h *= Prime;
                h ^= (byte)(c >> 8);
                h *= Prime;
            }
            return h;
        }

        /// <summary>16 lowercase hex digits.</summary>
        public static string ToHex(ulong hash)
        {
            const string digits = "0123456789abcdef";
            var chars = new char[16];
            for (int i = 15; i >= 0; i--)
            {
                chars[i] = digits[(int)(hash & 0xF)];
                hash >>= 4;
            }
            return new string(chars);
        }
    }
}
