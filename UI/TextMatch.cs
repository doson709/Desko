using System.Globalization;
using System.Text;

namespace Desko.UI
{
    // Search matching that ignores case and Vietnamese diacritics: "viet nam" finds "Việt Nam", "dao tao" finds "đào tạo".
    internal static class TextMatch
    {
        public static string Normalize(string s)
        {
            if (s.Length == 0) return s;
            var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(c == 'đ' ? 'd' : c);
            }
            return sb.ToString();
        }
    }
}
