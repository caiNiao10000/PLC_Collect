using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PlcDataHub.Core.Naming;

/// <summary>
/// 采集点显示名 → 数据库列名。
/// 规格 3.5 节：列名统一 ASCII；PostgreSQL 标识符上限是 63 <b>字节</b>（不是字符），
/// 中文列名会直接踩爆这个上限，因此中文一律转拼音。
/// </summary>
public static class ColumnNameGenerator
{
    /// <summary>PostgreSQL 标识符的硬上限，单位是字节。</summary>
    public const int MaxIdentifierBytes = 63;

    /// <summary>截断时为哈希后缀预留的字符数（下划线 + 8 位十六进制）。</summary>
    private const int HashSuffixLength = 9;

    /// <summary>
    /// 规范化单个显示名。不做重名处理——重名请用 <see cref="AssignUniqueColumns"/>。
    /// </summary>
    /// <exception cref="ArgumentException">显示名为空或全为无意义字符。</exception>
    public static string Normalize(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("显示名不能为空", nameof(displayName));
        }

        var sb = new StringBuilder(displayName.Length * 4);
        var index = 0;

        while (index < displayName.Length)
        {
            var ch = displayName[index];

            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                index++;
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                sb.Append(char.ToLowerInvariant(ch));
                index++;
            }
            else if (IsChinese(ch))
            {
                // 连续汉字必须整段交给拼音库：库的分隔符是"字符之间"的连接符，
                // 单字调用不会返回分隔符（GetPinyin("温") == "Wen"），
                // 逐字拼接会得到 "wendu" 而非规格 3.5 节要求的 "wen_du"。
                var start = index;
                while (index < displayName.Length && IsChinese(displayName[index]))
                {
                    index++;
                }

                sb.Append(ToPinyin(displayName[start..index]));
            }
            else
            {
                // 空格、#、%、-、. 等一律折叠为下划线，后续统一收敛连续下划线
                sb.Append('_');
                index++;
            }
        }

        var result = CollapseUnderscores(sb.ToString()).Trim('_');

        if (result.Length == 0)
        {
            throw new ArgumentException($"显示名 {displayName} 无法生成有效列名", nameof(displayName));
        }

        // 标识符不得以数字开头
        if (char.IsDigit(result[0]))
        {
            result = "p" + result;
        }

        return TruncateWithHash(result);
    }

    /// <summary>
    /// 为一组显示名批量分配唯一列名。重名按规格 3.5 节追加序号 _2、_3……
    /// </summary>
    public static IReadOnlyList<string> AssignUniqueColumns(IEnumerable<string> displayNames)
    {
        ArgumentNullException.ThrowIfNull(displayNames);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var displayName in displayNames)
        {
            var baseName = Normalize(displayName);
            var candidate = baseName;
            var suffix = 2;

            while (!used.Add(candidate))
            {
                // 加序号后可能再次超长，所以要按"给序号留位置"的方式截断
                var suffixText = "_" + suffix.ToString(CultureInfo.InvariantCulture);
                candidate = TruncateWithHash(baseName, reserveBytes: Encoding.UTF8.GetByteCount(suffixText)) + suffixText;
                suffix++;
            }

            result.Add(candidate);
        }

        return result;
    }

    private static bool IsChinese(char ch) => ch is >= '\u4e00' and <= '\u9fff';

    /// <summary>
    /// 一段连续汉字转无声调全拼，返回小写。
    /// 实测库行为（ToolGood.Words.Pinyin 3.0.1.4）：
    ///   GetPinyin("温度", "_", false) == "Wen_Du" —— 返回首字母大写，故此处统一 ToLowerInvariant；
    ///   splitSpan 是"字符之间"的连接符，单字调用不含分隔符；
    ///   tone:true 会得到带声调的 "Wēn_Dù"，故必须传 false。
    /// 输出统一改写为下划线分隔，不依赖库的分隔符取何种字符。
    /// </summary>
    private static string ToPinyin(string chineseRun)
    {
        var pinyin = ToolGood.Words.Pinyin.WordsHelper.GetPinyin(chineseRun, "_", false);

        if (string.IsNullOrEmpty(pinyin))
        {
            return string.Empty;
        }

        pinyin = pinyin.ToLowerInvariant();

        // 库已按 splitSpan='_' 连接，此处无需再改写分隔符；
        // 但若库对个别字符原样返回（超出其码表范围），结果会混入非 ASCII，故显式兜底。
        return string.Equals(pinyin, chineseRun, StringComparison.Ordinal)
            ? string.Empty
            : pinyin;
    }

    private static string CollapseUnderscores(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastWasUnderscore = false;

        foreach (var ch in value)
        {
            if (ch == '_')
            {
                if (!lastWasUnderscore)
                {
                    sb.Append(ch);
                }

                lastWasUnderscore = true;
            }
            else
            {
                sb.Append(ch);
                lastWasUnderscore = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 按 UTF-8 字节数截断，超出则保留前缀 + 下划线 + 8 位哈希。
    /// 用哈希而不是简单截断，是为了让两个长且前缀相同的名字截断后仍不冲突。
    /// </summary>
    private static string TruncateWithHash(string value, int reserveBytes = 0)
    {
        var budget = MaxIdentifierBytes - reserveBytes;

        if (Encoding.UTF8.GetByteCount(value) <= budget)
        {
            return value;
        }

        var hash = ShortHash(value);
        var keepBytes = budget - HashSuffixLength;
        var prefix = keepBytes > 0 ? TruncateToBytes(value, keepBytes) : string.Empty;

        return prefix + "_" + hash;
    }

    private static string TruncateToBytes(string value, int maxBytes)
    {
        var sb = new StringBuilder(value.Length);
        var used = 0;

        foreach (var ch in value)
        {
            var size = Encoding.UTF8.GetByteCount(ch.ToString());
            if (used + size > maxBytes)
            {
                break;
            }

            sb.Append(ch);
            used += size;
        }

        return sb.ToString();
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
