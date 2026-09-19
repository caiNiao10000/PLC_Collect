using FluentAssertions;
using PlcDataHub.Core;
using Xunit;

namespace PlcDataHub.Core.Tests;

public class TargetFrameworkTests
{
    [Fact]
    public void 目标框架必须是_net8_0()
    {
        // 规格 1.5 节：.NET 9/10 已放弃 Windows 10 支持。
        // 这个断言失败意味着有人升级了框架，会导致 Win10 工控机无法运行。
        TargetFrameworkProbe.TargetFramework.Should().Be(".NETCoreApp,Version=v8.0");
    }

    [Fact]
    public void 必须是_windows_变体而不是裸_net8_0()
    {
        // 若有人把 TFM 从 net8.0-windows 改成 net8.0，这条会失败。
        TargetFrameworkProbe.SupportedPlatform.Should().Be("Windows7.0");
    }
}
