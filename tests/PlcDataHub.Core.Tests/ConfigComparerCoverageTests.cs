using System.Reflection;
using System.Text;
using FluentAssertions;
using PlcDataHub.Core.Model;
using Xunit;

namespace PlcDataHub.Core.Tests;

/// <summary>
/// ConfigComparer 的比较字段是手写清单。若给模型新增字段却忘记同步比较器，
/// 该字段的差异会被判为"无变化" —— 热加载跳过重建，表现为"改了配置不生效"
/// 且不报错。本测试用于在新增字段时立刻失败，强制同步。
/// <para>
/// 匹配范围是"该类型对应比较方法的<b>方法体</b>"，且要求属性名以 <c>left.X</c> / <c>right.X</c>
/// 形式出现。方法体在匹配前会<b>先剥离注释</b> —— 只取方法体并不够：
/// 把比较行用 <c>//</c> 注释掉后，该行文本仍位于方法体范围内，
/// 不剥注释的话测试照样通过，而这一行的比较能力已经没了
/// （实测见 task-2-report.md 修复轮 3 的 M-A2）。
/// </para>
/// <para>
/// 代价（控制者已裁定接受、不修）：本测试通过读取<b>仓库源码文件</b>核对比较器，
/// 因此必须在含源码的仓库内运行 —— 在没有源码的产物目录里运行会失败。
/// 将来若引入"产物级测试"，需改为把源码作为 EmbeddedResource 嵌入测试程序集。
/// </para>
/// </summary>
public class ConfigComparerCoverageTests
{
    /// <summary>
    /// 有意不参与配置比较的属性及原因。新增条目必须写明理由 ——
    /// 这个白名单是"经过思考的豁免"，不是"绕过测试的逃生口"。
    /// </summary>
    private static readonly Dictionary<string, string> IntentionallySkipped = new()
    {
        // ConfigComparer 当前比较了全部语义字段，暂无豁免。
        // 示例格式：["SomeField"] = "为什么它不影响采集行为",
    };

    /// <summary>
    /// 每个模型类型由哪个比较方法负责逐字段比较。
    /// 注意两者不同：<see cref="PointConfig"/> 在 PointEquivalent，<see cref="PollGroup"/> 在 GroupsEquivalent。
    /// 若将来把比较逻辑挪进别的辅助方法，需要同步这里（这是刻意的摩擦，避免绕过方法体检查）。
    /// </summary>
    private static readonly Dictionary<Type, string> ComparingMethodByType = new()
    {
        [typeof(PointConfig)] = "PointEquivalent",
        [typeof(PollGroup)] = "GroupsEquivalent",
        [typeof(S7Address)] = "S7Equivalent",
        [typeof(ModbusAddress)] = "ModbusEquivalent",
    };

    [Fact]
    public void ConfigComparer_必须比较模型的所有公开属性()
    {
        var uncheckedProperties = new List<string>();

        foreach (var (type, methodName) in ComparingMethodByType)
        {
            // 只看该类型对应比较方法的方法体，注释里的属性名不算数
            var body = ReadMethodBody("ConfigComparer.cs", methodName);

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (IntentionallySkipped.ContainsKey(property.Name))
                {
                    continue;
                }

                if (!body.Contains($"left.{property.Name}", StringComparison.Ordinal)
                    && !body.Contains($"right.{property.Name}", StringComparison.Ordinal))
                {
                    uncheckedProperties.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        uncheckedProperties.Should().BeEmpty(
            "ConfigComparer 必须在对应比较方法的方法体里逐字段比较模型的所有属性"
            + "（要求以 left.X / right.X 形式出现，注释里提到不算）。"
            + "新增属性后请同步更新 ConfigComparer，或在本测试的 IntentionallySkipped 里登记并写明豁免理由。"
            + "未覆盖的属性会让该字段的配置变更被误判为'无变化'，导致热加载跳过重建。");
    }

    [Fact]
    public void 豁免白名单的每个条目都必须写明理由()
    {
        // 注意：这里不能用 OnlyContain —— FluentAssertions 6.12.1 的 OnlyContain 在
        // 空集合上会失败（"but the collection is empty"），而"零条目"本身是合规状态。
        // 改为"挑出不合规条目并断言其为空"：空集合与全部合规都能正确通过，
        // 只要有一条理由缺失或过短就会失败。
        var entriesWithoutReason = IntentionallySkipped
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value) || entry.Value.Length < 8)
            .Select(entry => entry.Key)
            .ToList();

        entriesWithoutReason.Should().BeEmpty(
            "豁免必须写明理由，否则白名单会退化成绕过测试的后门");
    }

    [Fact]
    public void 豁免白名单的键必须都是模型中真实存在的属性()
    {
        // 防止删属性后留下"僵尸豁免"—— 它永远不生效、也永远不报错
        var allPropertyNames = ComparingMethodByType.Keys
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var zombieKeys = IntentionallySkipped.Keys
            .Where(key => !allPropertyNames.Contains(key))
            .ToList();

        zombieKeys.Should().BeEmpty(
            "豁免白名单里出现了模型中不存在的属性名。这通常是删除了模型属性却忘了清理豁免 —— "
            + "僵尸豁免永远不会生效、也永远不会报错，会让白名单悄悄失效。");
    }

    private static string ReadSourceFile(string fileName)
    {
        // 从测试程序集位置回溯到仓库根，再定位源文件
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PlcDataHub.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("找不到仓库根（含 PlcDataHub.sln 的目录），无法读取比较器源码");

        var path = Path.Combine(directory!.FullName, "src", "PlcDataHub.Core", "Model", fileName);
        File.Exists(path).Should().BeTrue($"源文件应存在：{path}");

        return File.ReadAllText(path);
    }

    /// <summary>
    /// 去掉源码里的注释（<c>//</c> 行注释与 <c>/* */</c> 块注释），字符串与字符字面量原样保留。
    /// <b>必须先剥注释再匹配</b>：把比较行注释掉后它仍在方法体范围内，
    /// 不剥注释就会把"已经失去比较能力的行"判为已覆盖。
    /// </summary>
    private static string StripComments(string source)
    {
        var builder = new StringBuilder(source.Length);
        var inLineComment = false;
        var inBlockComment = false;
        var inString = false;
        var inChar = false;
        var inVerbatimString = false;

        for (var i = 0; i < source.Length; i++)
        {
            var current = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\n')
                {
                    inLineComment = false;
                    builder.Append(current);
                }

                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                else if (current == '\n')
                {
                    builder.Append(current);
                }

                continue;
            }

            if (inVerbatimString)
            {
                builder.Append(current);
                if (current == '"' && next == '"')
                {
                    builder.Append(next);
                    i++;
                }
                else if (current == '"')
                {
                    inVerbatimString = false;
                }

                continue;
            }

            if (inString)
            {
                builder.Append(current);
                if (current == '\\' && next != '\0')
                {
                    builder.Append(next);
                    i++;
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inChar)
            {
                builder.Append(current);
                if (current == '\\' && next != '\0')
                {
                    builder.Append(next);
                    i++;
                }
                else if (current == '\'')
                {
                    inChar = false;
                }

                continue;
            }

            if (current == '/' && next == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (current == '@' && next == '"')
            {
                inVerbatimString = true;
                builder.Append(current);
                builder.Append(next);
                i++;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                builder.Append(current);
                continue;
            }

            if (current == '\'')
            {
                inChar = true;
                builder.Append(current);
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 找到指定方法<b>声明</b>处的方法体起点（表达式体 <c>=&gt;</c> 的 <c>=</c>，或块体的 <c>{</c>）。
    /// <para>
    /// 不能直接用第一次文本出现来定位：辅助方法会先以<b>调用点</b>的形式出现在别的方法体里
    /// （例如 PointEquivalent 里的 <c>S7Equivalent(left.S7, right.S7)</c>），
    /// 只取 <c>IndexOf</c> 会把调用点当成声明。
    /// 实测表现：报"方法体应当是表达式体（以 '=&gt;' 开始）, but found &amp;"。
    /// </para>
    /// 判定规则：配平参数表之后，紧随其后（跳过空白）是 <c>=&gt;</c> 或 <c>{</c> 的才是声明；
    /// 调用点后面通常是 <c>;</c> / <c>)</c> / <c>&amp;</c>，会被跳过继续往后找。
    /// </summary>
    private static int FindMethodBodyStart(string source, string methodName)
    {
        var searchFrom = 0;

        while (true)
        {
            var candidate = source.IndexOf($"{methodName}(", searchFrom, StringComparison.Ordinal);
            if (candidate < 0)
            {
                throw new InvalidOperationException($"ConfigComparer 里找不到 {methodName} 的方法声明");
            }

            // 配平该候选位置的参数表
            var cursor = source.IndexOf('(', candidate);
            var parenDepth = 0;
            for (; cursor < source.Length; cursor++)
            {
                if (source[cursor] == '(')
                {
                    parenDepth++;
                }
                else if (source[cursor] == ')')
                {
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        break;
                    }
                }
            }

            if (cursor >= source.Length)
            {
                throw new InvalidOperationException($"{methodName} 的参数表未正常闭合");
            }

            var after = cursor + 1;
            while (after < source.Length && char.IsWhiteSpace(source[after]))
            {
                after++;
            }

            var isExpressionBody = after + 1 < source.Length
                && source[after] == '='
                && source[after + 1] == '>';

            if (isExpressionBody || (after < source.Length && source[after] == '{'))
            {
                return after;
            }

            searchFrom = candidate + methodName.Length;
        }
    }

    /// <summary>
    /// 提取指定方法的方法体源码。同时支持<b>表达式体</b>（<c>=&gt; …;</c>）与<b>块体</b>（<c>{ … }</c>），
    /// 因为 ConfigComparer 里两种写法都有：PointEquivalent / GroupsEquivalent / S7Equivalent /
    /// ModbusEquivalent 是表达式体，PointsEquivalent 是块体。
    /// </summary>
    private static string ReadMethodBody(string fileName, string methodName)
    {
        var source = StripComments(ReadSourceFile(fileName));
        var cursor = FindMethodBodyStart(source, methodName);

        if (source[cursor] == '{')
        {
            var bodyStart = cursor;
            var braceDepth = 0;
            for (; cursor < source.Length; cursor++)
            {
                if (source[cursor] == '{')
                {
                    braceDepth++;
                }
                else if (source[cursor] == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0)
                    {
                        return source[bodyStart..(cursor + 1)];
                    }
                }
            }

            throw new InvalidOperationException($"{methodName} 的方法体未正常闭合");
        }

        // 表达式体：从 '=>' 起，到同层的 ';' 为止
        source[cursor].Should().Be('=', $"{methodName} 的方法体应当是表达式体（以 '=>' 开始）");

        var expressionStart = cursor;
        var roundDepth = 0;
        var squareDepth = 0;
        var curlyDepth = 0;
        for (; cursor < source.Length; cursor++)
        {
            switch (source[cursor])
            {
                case '(':
                    roundDepth++;
                    break;
                case ')':
                    roundDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    squareDepth--;
                    break;
                case '{':
                    curlyDepth++;
                    break;
                case '}':
                    curlyDepth--;
                    break;
                case ';' when roundDepth == 0 && squareDepth == 0 && curlyDepth == 0:
                    return source[expressionStart..(cursor + 1)];
                default:
                    break;
            }
        }

        throw new InvalidOperationException($"{methodName} 的表达式体未正常结束（找不到同层分号）");
    }
}
