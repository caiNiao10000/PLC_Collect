using System.Reflection;
using FluentAssertions;
using PlcDataHub.Core.Model;
using Xunit;

namespace PlcDataHub.Core.Tests;

/// <summary>
/// ConfigComparer 的比较字段是手写清单。若给模型新增字段却忘记同步比较器，
/// 该字段的差异会被判为"无变化" —— 热加载跳过重建，表现为"改了配置不生效"
/// 且不报错。本测试用于在新增字段时立刻失败，强制同步。
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

    [Fact]
    public void ConfigComparer_必须比较模型的所有公开属性()
    {
        // 比较器的源码文本 —— 属性名出现在其中即视为被比较
        var comparerSource = ReadSourceFile("ConfigComparer.cs");

        var uncheckedProperties = new List<string>();

        foreach (var type in new[] { typeof(PointConfig), typeof(PollGroup) })
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (IntentionallySkipped.ContainsKey(property.Name))
                {
                    continue;
                }

                if (!comparerSource.Contains(property.Name, StringComparison.Ordinal))
                {
                    uncheckedProperties.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        uncheckedProperties.Should().BeEmpty(
            "ConfigComparer 必须比较模型的所有属性。新增属性后请同步更新 ConfigComparer，"
            + "或在本测试的 IntentionallySkipped 里登记并写明豁免理由。"
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
}
