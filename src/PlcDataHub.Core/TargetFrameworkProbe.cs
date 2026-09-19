namespace PlcDataHub.Core;

/// <summary>
/// 目标框架探针。存在的唯一目的是让"框架必须是 net8.0-windows"这条约束
/// 变成一个会失败的测试，而不是一条只写在文档里的约定。
/// </summary>
public static class TargetFrameworkProbe
{
    /// <summary>编译时目标框架，实测值为 ".NETCoreApp,Version=v8.0"。</summary>
    public static string TargetFramework => ReadAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?
        .FrameworkName ?? throw new InvalidOperationException("找不到 TargetFrameworkAttribute");

    /// <summary>编译时支持的最低操作系统平台，net8.0-windows 下实测为 "Windows7.0"。</summary>
    public static string SupportedPlatform => ReadAttribute<System.Runtime.Versioning.SupportedOSPlatformAttribute>()?
        .PlatformName ?? throw new InvalidOperationException("找不到 SupportedOSPlatformAttribute");

    private static T? ReadAttribute<T>() where T : Attribute =>
        (T?)Attribute.GetCustomAttribute(typeof(TargetFrameworkProbe).Assembly, typeof(T));
}
