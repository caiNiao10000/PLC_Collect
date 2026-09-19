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
}
