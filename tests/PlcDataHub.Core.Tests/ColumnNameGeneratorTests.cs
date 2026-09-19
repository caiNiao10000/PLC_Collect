using System.Text;
using FluentAssertions;
using PlcDataHub.Core.Naming;
using Xunit;

namespace PlcDataHub.Core.Tests;

public class ColumnNameGeneratorTests
{
    [Theory]
    [InlineData("Temp Kiln", "temp_kiln")]
    [InlineData("PID_输出%", "pid_shu_chu")]
    [InlineData("温度", "wen_du")]
    [InlineData("1#窑尾温度", "p1_yao_wei_wen_du")]
    public void 规范化规则符合规格_3_5_节(string input, string expected)
    {
        ColumnNameGenerator.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void 结果必须以字母或下划线开头()
    {
        ColumnNameGenerator.Normalize("123abc").Should().StartWith("p");
    }

    [Fact]
    public void 结果只含小写字母数字下划线()
    {
        var result = ColumnNameGenerator.Normalize("A-B.C/D E中文");
        result.Should().MatchRegex("^[a-z0-9_]+$");
    }

    [Fact]
    public void 超过_63_字节时截断并追加哈希()
    {
        var longName = new string('a', 100);
        var result = ColumnNameGenerator.Normalize(longName);

        Encoding.UTF8.GetByteCount(result).Should().BeLessOrEqualTo(63);
        result.Should().MatchRegex("^a+_[0-9a-f]{8}$");
    }

    [Fact]
    public void 同一批内重名自动追加序号()
    {
        var result = ColumnNameGenerator.AssignUniqueColumns(new[] { "温度", "温度", "温度" });

        result.Should().Equal("wen_du", "wen_du_2", "wen_du_3");
    }

    [Fact]
    public void 重名避让后仍满足_63_字节上限()
    {
        var longName = new string('b', 100);
        var result = ColumnNameGenerator.AssignUniqueColumns(new[] { longName, longName });

        result.Should().HaveCount(2);
        result.Should().OnlyHaveUniqueItems();
        foreach (var name in result)
        {
            Encoding.UTF8.GetByteCount(name).Should().BeLessOrEqualTo(63);
        }
    }

    [Fact]
    public void 空输入抛出异常而不是静默产生空列名()
    {
        var act = () => ColumnNameGenerator.Normalize("   ");
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// 回归：拼音库对码表外的汉字是"段内逐个透传"（GetPinyin("温鿖") == "Wen_鿖"），
    /// 不是整段失败。旧实现用"整段是否等于输入"判断转出成功，漏掉了部分透传的情形，
    /// 导致非 ASCII 混进列名。本用例锁定"结果恒为纯 ASCII"。
    /// U+9FD6..U+9FFF 共 42 个码点会以这种形态透传。
    /// </summary>
    [Theory]
    [InlineData("温\u9FD6", "wen")]
    [InlineData("PID\u9FD6输出", "pid_shu_chu")]
    [InlineData("\u9FEA温度", "wen_du")]
    [InlineData("温\u9FFF", "wen")]
    public void 码表外汉字透传时结果仍为纯_ASCII(string input, string expected)
    {
        var result = ColumnNameGenerator.Normalize(input);

        result.Should().Be(expected);
        result.Should().MatchRegex("^[a-z0-9_]+$");
    }

    [Fact]
    public void 码表外汉字不会让不同显示名静默撞名()
    {
        // "PID\u9FD6输出" 若泄漏成 "pid鿖_shu_chu" 则是非 ASCII；
        // 修复后两者都必须纯 ASCII，且仍是两个不同列名（不得被静默合并）。
        var result = ColumnNameGenerator.AssignUniqueColumns(new[] { "PID\u9FD6输出", "PID温度" });

        result.Should().OnlyHaveUniqueItems();
        foreach (var name in result)
        {
            name.Should().MatchRegex("^[a-z0-9_]+$");
        }
    }

    /// <summary>
    /// 回归：避让序号会进位（_9→_10、_99→_100、_999→_1000），
    /// 每一步都要重新按"给序号留位置"的字节预算截断。
    /// 若 reserveBytes 被写死成 2，本用例会在 _10 处产出 64 字节而失败。
    /// </summary>
    [Fact]
    public void 序号进位到两位数以上时仍满足_63_字节上限()
    {
        var longName = new string('b', 100);
        var result = ColumnNameGenerator.AssignUniqueColumns(Enumerable.Repeat(longName, 12));

        result.Should().HaveCount(12);
        result.Should().OnlyHaveUniqueItems();
        result[10].Should().EndWith("_11");
        result[11].Should().EndWith("_12");
        foreach (var name in result)
        {
            Encoding.UTF8.GetByteCount(name).Should().BeLessOrEqualTo(63);
        }
    }

    /// <summary>
    /// 回归：截断点可能正好落在下划线之后，拼接哈希时不得产出连续下划线。
    /// </summary>
    [Fact]
    public void 截断后不会产生连续下划线()
    {
        var input = new string('a', 53) + "_" + new string('b', 100);
        var result = ColumnNameGenerator.Normalize(input);

        Encoding.UTF8.GetByteCount(result).Should().BeLessOrEqualTo(63);
        result.Should().NotContain("__");
    }

    /// <summary>
    /// 回归：锁定库的**第二种失败形态**。码表外汉字有两种，本用例专测"整字丢弃"那一种：
    ///   U+9FD3         → 整字被丢弃，**不泄漏**非 ASCII（本用例）
    ///   U+9FD6..U+9FFF → 段内逐个透传，会泄漏非 ASCII（由 码表外汉字透传时结果仍为纯_ASCII 覆盖）
    /// 实测丢弃发生在库内部（不是本类的空串分支）：GetPinyin("\u9FD3") == ""，
    /// 而放在连续汉字串里时库直接跳过该字并返回其余音节，如 GetPinyin("\u9FD3输出") == "Shu_Chu"。
    /// 丢弃是安全降级（列名仍为纯 ASCII），代价是该字消失、其两侧的分隔符也随之合并。
    /// </summary>
    [Fact]
    public void 库无法转换的汉字被丢弃而不是泄漏非ASCII()
    {
        const string unconvertible = "\u9FD3";

        var withChar = ColumnNameGenerator.Normalize("PID" + unconvertible + "输出");
        var withoutChar = ColumnNameGenerator.Normalize("PID输出");

        // 该字被丢弃，且丢弃后**没有留下分隔符**，故两侧音节直接相接。
        withChar.Should().Be("pidshu_chu");
        withChar.Should().MatchRegex("^[a-z0-9_]+$");

        // 丢弃是全路径一致的：无论该字是自成一段还是夹在连续汉字段内，都不会留下分隔符。
        //   "A\u9FD3B"  -> 该字自成一段，库返回空串 -> 本类空串分支丢弃，两侧字母直接相接
        //   "\u9FD3输出" -> 库在段内跳过该字 -> 结果同样无分隔符
        ColumnNameGenerator.Normalize("A" + unconvertible + "B").Should().Be("ab");
        ColumnNameGenerator.Normalize(unconvertible + "温度").Should().Be("wen_du");
        ColumnNameGenerator.Normalize(unconvertible + "输出").Should().Be("shu_chu");

        // 代价：不含该字的显示名会得到**同一个基名**（Normalize 不做去重）。
        withChar.Should().Be(withoutChar, "该字被丢弃后与不含它的名字得到同一基名");

        // 兜底去重在 AssignUniqueColumns：相同基名追加 _2，不会产出重复列名。
        var assigned = ColumnNameGenerator.AssignUniqueColumns(new[] { "PID" + unconvertible + "输出", "PID输出" });
        assigned.Should().Equal("pidshu_chu", "pidshu_chu_2");
    }
}
