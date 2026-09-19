# Plan 1：核心闭环（工程骨架 + 迁移引擎 + Modbus + 存储 + 采集器）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 打通"配置 → 自动建表 → 采集 → 入库"的端到端最小闭环，用 Modbus TCP 作为第一个协议，程序形态是控制台采集器（可跑通、可验证），为后续 Web 界面与 S7 协议打好全部地基。

**Architecture:** 分层但**不做过度抽象**。`Core` 放纯逻辑（模型、列名生成、DDL 生成、迁移差异引擎），无任何外部依赖，可 100% 单元测试；`Protocols` 用 `IPlcConnection` 抽象协议，先实现 Modbus TCP 与"假连接"；`Storage` 负责 Npgsql 访问、迁移执行、批量写入；`Collector` 是宿主，串起"连接 → 采集 → 解码 → 入库"。

**Tech Stack:** .NET 8（`net8.0-windows`，**锁定，禁止升级**）、Npgsql、NModbus、xUnit + FluentAssertions、Serilog

**Spec:** `docs/specs/2026-02-09-plc-datahub-design.md`

## Global Constraints

以下约束来自规格，**每个任务都隐含包含**，不再逐条重复：

- **目标框架必须为 `net8.0-windows`**，不得升级到 net9.0 / net10.0（规格 1.5 节）。.NET 9/10 已放弃 Windows 10 支持，一旦升级则 Win10 工控机无法运行。本机装有 .NET 10 运行时，此风险实际存在。
- **运行架构 `win-x64`**，只发布 64 位包（规格 1.5 节）。
- **命名约定**：目录与远端仓库名为 `PLC_Collect`；**C# 命名空间一律用 `PlcDataHub.*`**（不用 `PLC_Collect` / `PlcCollect`），因为 C# 命名空间带下划线不符合 .NET 命名规范（规格 1.5 节）。
- **远端同步**：`origin` = `https://github.com/caiNiao10000/PLC_Collect.git`；本地为主、每任务提交、阶段完成即推送（规格 1.5 节）。
- **采集数据表时间字段**用 `timestamp`（无时区）存本地时间；**配置与状态表**用 `timestamptz`（规格 1.4 节）。
- **数据库 schema 划分**：`cfg` 配置 / `rt` 运行状态 / `d` 采集数据（规格 3.2）。
- **列名统一 ASCII**，超长按 54 字符 + `_` + 8 位哈希截断（PostgreSQL 标识符上限 63 **字节**）（规格 3.5）。
- **采集点数据列不设主键、不设唯一约束**，只建 `ts` 索引（规格 3.4）。
- **迁移必须幂等**：同一份配置连续执行两次，第二次不产生任何 DDL（规格 4.3，验收项 A12）。
- **绝不静默丢数据**：删点默认软删除、类型冲突硬拦截、迁移单事务执行（规格 4.2）。
- **编码统一 UTF-8**，C# 文件用 `\n` 换行，文件末尾留空行。仓库已有 `.gitattributes`（`* text=auto eol=lf`）强制 LF，不要改它。
- **⚠️ 用 PowerShell 改写含中文的源文件时必须显式指定 UTF-8，并做字节级校验。**
  Task 1 实测踩坑：`Get-Content -Raw` + `Set-Content`（未指定 `-Encoding`）在 PowerShell 5.1 下按**系统 ANSI 代码页（CP936/GBK）**写出，把 `Directory.Build.props` 里的 7 个中文字符写成了非法 UTF-8（`覆盖` 的 `覆` → `E7 9B 3F`、`。` → 半角 `?`），且**没有任何报错**。
  正确做法：用 `[System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))`；
  写完必须复验「字节数 + 无 U+FFFD + 纯 LF + 末尾换行 + 关键中文串命中」。
  **注意：控制台显示中文乱码往往只是代码页渲染问题，不代表文件损坏——判断文件是否损坏必须看字节，不能靠肉眼比对终端输出。**
  更稳妥的做法是优先用文件写入工具而非 shell 改写源码。
- **PowerShell 脚本必须兼容 PowerShell 5.1**，禁止 7.x 专有语法（规格 1.5）。
- **禁止 `git add -A` / `git add .`，一律显式列出本任务改动的路径。**
  原因：本计划的任务可能由并行 subagent 执行、共享同一个工作树；后提交者会把 HEAD 推移而先开工者不知情。
  若使用 `git add -A`，会把他人未提交的改动裹进自己的提交，且**提交信息会完全误导后来的人**。
  （此风险实际发生过一次：Task 2 的文档修复把 HEAD 从 `bda6a5d` 推到 `818de1e`，而并行的代码修复基线记的是 `bda6a5d`。）
- **变异/探针实验收尾必须清理自己制造的残留**（含 `bin`/`obj` 下的异常目录、`Release`/`publish` 产物、临时探针文件），并复验工作树干净。
- **凡是要枚举类型成员，一律用反射，不要用正则或手工清单。**
  实测教训：控制者曾用正则抽取模型属性得到 18 个，反射实测为 19 个（正则的行尾锚点在每个 record 的最后一个参数上失配，且手工清单里重复写了一项，两个错误凑巧抵消成一个"看起来合理"的数字）。
- **⚠️ 凡 fixture 细节（元素下标、列序、期望值）一律先实测再写断言**：先跑红 → 打印/断言真实值 → 再写期望。
  Task 4 的实现者在同一任务里因"凭推理写 fixture 细节"错了 **4 次**（例如把自动点的下标写成 `names[3]`，实际是 `names[5]`，前 3 个是固定列 ts/q/src_ts）。**每一次都是靠"先跑红→看真实值"发现的。**
- **⚠️ 验证脚本一律用绝对路径或显式设置工作目录。**
  本项目已有多起因此产生的假结果，其中**控制者自己就犯了两次**：`ReadAllText` 因相对路径抛异常 → 变异根本没注入 → 输出的 `build exit=0` 是空转的假结果。凡是"注入变异再看结果"的脚本，**必须在注入后断言目标文件真的被改动**（例如再次读取并检查匹配计数），并在构建后**断言 exit code**。
- **⚠️ 变异不可观测时，先怀疑变异不可达，而不是断定测试有鉴别力。** 正确反应是插桩定位真实代码路径，不是换个变异重试。
  （Task 3 实例：某处改了三种方式都 `18/18 全绿`，插桩后才发现那段代码路径**根本没被执行**。）
- **⚠️ 不要从"我的 fixture 覆盖不到"外推成"不存在这种输入"。** 这是**全称结论的越界外推**，比工具用错更根本。
  （Task 4 实例：实现者的 A/B fixture 只放了一个手工名，据此断言"`suffix++` 在所有可构造输入下都不影响输出"，并建议删掉那段递增——实测该递增是**终止性承重件**，删掉会死循环。）
  推论：一个论证证明了 A，**不等于**它证明了 A ∧ B。接受论证时要追问"它没证明的那一半是什么"。
- **变异必须产生"可编译 + 断言失败"的结果**，且红色必须用**该变异独有的失败签名**核对。
  （实例：把异常类型换成没有三参构造的类型 → 编译失败被误当成"变异被检出"；`dotnet test` 未重建 → 跑到上一次变异的陈旧二进制。）
- **⚠️ 变异脚本执行还原后必须强制重建，否则后续所有"红"都不可信。**
  根因：`Copy-Item` 还原会把备份的**旧 mtime** 带给源文件，MSBuild 于是认为被测程序集仍是最新而不重建，但测试项目会重建 → 变成"**新测试 + 旧被测代码**"。
  （Task 5 实例：收尾时两条用例突然变红，实现者一度按"实现有 bug"去读代码，`--no-incremental` 后立刻全绿。）
- **⚠️ "被拒绝"类断言必须断言具体消息或具体异常类型，不能只断言"抛了某个异常"。**
  否则**任何其它原因的异常都能满足它 → 假绿**。
  （Task 5 实例：变异 M15 恢复"过度守卫"后，只断言 `InvalidOperationException` 的用例全部仍通过；收紧为断言消息后才精确变红。）
- 所有提交信息用中文，格式 `feat:` / `test:` / `fix:` / `chore:` / `docs:`。

---

## 文件结构

本计划要建立的文件（每个文件一个明确职责）：

```
D:\deepseek\PLC_Collect\
├─ Directory.Build.props                    ★ 全工程锁定 net8.0-windows
├─ PlcDataHub.sln
├─ src\
│  ├─ PlcDataHub.Core\                      纯逻辑，零外部依赖
│  │  ├─ PlcDataHub.Core.csproj
│  │  ├─ Model\
│  │  │  ├─ Enums.cs                        ProtocolKind / S7Area / ModbusRegisterArea / PointDataType / ColumnType / ConnectionState
│  │  │  ├─ DeviceConnection.cs
│  │  │  ├─ PollGroup.cs
│  │  │  ├─ PointConfig.cs
│  │  │  └─ DesiredSchema.cs                期望结构的不可变表示（表名 → 列集合）
│  │  ├─ Naming\ColumnNameGenerator.cs      中文转拼音、ASCII 规范化、冲突避让、63 字节截断
│  │  ├─ Schema\SqlTypeMapper.cs            PointDataType → PostgreSQL 类型
│  │  ├─ Schema\DesiredSchemaBuilder.cs     配置图 → DesiredSchema
│  │  └─ Migration\
│  │     ├─ ColumnDefinition.cs             现存列的元数据
│  │     ├─ MigrationPlan.cs                差异计划（不可变）+ IsEmpty
│  │     ├─ MigrationStep.cs                单条 DDL + 是否破坏性 + 影响说明
│  │     └─ MigrationPlanner.cs             期望结构 vs 现有结构 → MigrationPlan
│  ├─ PlcDataHub.Protocols\
│  │  ├─ PlcDataHub.Protocols.csproj
│  │  ├─ IPlcConnection.cs                  协议抽象 + ReadBlock / ReadResult
│  │  ├─ ReadBlockPlanner.cs                读块合并算法（协议无关，按窗口合并）
│  │  ├─ ByteDecoder.cs                     字节数组 → 工程值（大小端、REAL、BOOL）
│  │  ├─ Modbus\ModbusTcpConnection.cs
│  │  └─ Fake\FakePlcConnection.cs          可编程假连接，供测试与无设备开发
│  ├─ PlcDataHub.Storage\
│  │  ├─ PlcDataHub.Storage.csproj
│  │  ├─ NpgsqlConnectionFactory.cs
│  │  ├─ SchemaIntrospector.cs              读 information_schema → 现有结构
│  │  ├─ MigrationExecutor.cs               执行 MigrationPlan（单事务 + ANALYZE）
│  │  └─ DataWriter.cs                      批量 INSERT 采集数据
│  └─ PlcDataHub.Collector\
│     ├─ PlcDataHub.Collector.csproj
│     ├─ Program.cs                         宿主入口（本计划先做控制台模式）
│     ├─ Config\CollectorOptions.cs
│     ├─ Config\JsonConfigLoader.cs         从 JSON 文件读配置（Web 界面尚未存在时的过渡）
│     ├─ Runtime\GroupScheduler.cs          每组的 next_due 调度（落后即跳过）
│     └─ Runtime\GroupCollector.cs          单组采集循环：读 → 解码 → 交写入管道
└─ tests\
   ├─ PlcDataHub.Core.Tests\
   │  ├─ PlcDataHub.Core.Tests.csproj
   │  ├─ ColumnNameGeneratorTests.cs
   │  ├─ DesiredSchemaBuilderTests.cs
   │  └─ MigrationPlannerTests.cs
   ├─ PlcDataHub.Protocols.Tests\
   │  ├─ PlcDataHub.Protocols.Tests.csproj
   │  ├─ ReadBlockPlannerTests.cs
   │  └─ ByteDecoderTests.cs
   └─ PlcDataHub.Integration.Tests\
      ├─ PlcDataHub.Integration.Tests.csproj
      ├─ TestDatabase.cs                    真实 PG 连接（环境变量未设则跳过）
      ├─ MigrationExecutorTests.cs
      └─ CollectorEndToEndTests.cs
```

**职责边界说明**（避免后续任务混淆）：
- `Core` **不知道**数据库连接、不知道 PLC、不知道网络。它只做"配置进、SQL 出"的纯计算。
- `Protocols` **不知道**数据库，只负责"给我一组地址，我返回字节和值"。
- `Storage` **不知道** PLC，只负责"把行写进去"和"把表结构改成这样"。
- `Collector` 是唯一把三者串起来的地方。

---

## Task 1：工程骨架与目标框架锁定

对应规格 1.5 节核心约束。**这是整个项目风险最高的一条**：框架被误升级只在 Windows 10 现场部署时才暴露。

**Files:**
- Create: `Directory.Build.props`
- Create: `PlcDataHub.sln`
- Create: `src/PlcDataHub.Core/PlcDataHub.Core.csproj`
- Create: `tests/PlcDataHub.Core.Tests/PlcDataHub.Core.Tests.csproj`
- Create: `src/PlcDataHub.Core/TargetFrameworkProbe.cs`
- Create: `tests/PlcDataHub.Core.Tests/TargetFrameworkTests.cs`

**Interfaces:**
- Consumes: 无（第一个任务）
- Produces: 可编译的解决方案骨架；`Directory.Build.props` 中锁定的 `net8.0-windows`，后续所有 csproj 自动继承

- [ ] **Step 1: 确认工具链（本机已实测，不需要 .NET 8 SDK）**

Run:
```powershell
dotnet --list-sdks
dotnet --list-runtimes | Select-String 'NETCore.App 8\.'
```

Expected: SDk 有 **10.0.401** 或任意版本即可；运行时必须有 **8.0.x**。

> **实测结论（2026-02-09 在本机验证）**：**不需要安装 .NET 8 SDK**。
> 本机只有 .NET 10 SDK（10.0.401），配合已装的 .NET 8.0.22 运行时，
> 可以正常构建并测试 `net8.0-windows` 目标——.NET 8 的引用包由 NuGet 自动获取，
> 测试实际运行在 .NET 8.0.22 上。
> 已验证：`dotnet build` 输出 `bin\Debug\net8.0-windows\win-x64\`，`dotnet test` 通过。
> 因此本步骤**不需要停下来装 SDK**，除非 `dotnet --list-runtimes` 里没有 8.0.x。

- [ ] **Step 2: 创建 `Directory.Build.props`**

路径：`D:\deepseek\PLC_Collect\Directory.Build.props`

```xml
<Project>
  <!--
    全工程统一构建属性。任何 csproj 都不得单独覆盖 TargetFramework。
    原因见 docs/specs/2026-02-09-plc-datahub-design.md 1.5 节：
    .NET 9 / .NET 10 已放弃 Windows 10 支持，升级会导致 Win10 工控机无法运行。
    本机装有 .NET 10 运行时，此风险实际存在。
  -->
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>12</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <InvariantGlobalization>false</InvariantGlobalization>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>
</Project>
```

> 说明：`InvariantGlobalization=false` 是必须的——`ColumnNameGenerator` 要用到中文与拼音处理，不能启用固定区域性。`EnableWindowsTargeting=true` 让 `net8.0-windows` 在非 Windows 构建代理上也能还原包。

- [ ] **Step 3: 创建 `PlcDataHub.Core.csproj`**

路径：`src/PlcDataHub.Core/PlcDataHub.Core.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- 刻意不写 TargetFramework：由 Directory.Build.props 统一提供 -->
  <PropertyGroup>
    <RootNamespace>PlcDataHub.Core</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <!--
      必须排除 tests 子目录。SDK 风格项目默认把目录树下的所有 .cs 都编译进来，
      如果测试项目建在本项目的子目录里，测试源码会被编进类库并报
      "未能找到类型或命名空间名 Xunit" —— 而且错误会指向类库，极难排查。
      本机已实测踩到这个坑。见 Task 6 / Task 8 的模板生成命令。
    -->
    <Compile Remove="tests\**" />
    <None Remove="tests\**" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: 创建框架探针与测试项目**

路径：`src/PlcDataHub.Core/TargetFrameworkProbe.cs`

```csharp
namespace PlcDataHub.Core;

/// <summary>
/// 目标框架探针。存在的唯一目的是让"框架必须是 net8.0-windows"这条约束
/// 变成一个会失败的测试，而不是一条只写在文档里的约定。
/// </summary>
public static class TargetFrameworkProbe
{
    /// <summary>返回本程序集编译时使用的目标框架名，实测值为 ".NETCoreApp,Version=v8.0"。</summary>
    public static string TargetFramework =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
            is [System.Runtime.Versioning.TargetFrameworkAttribute attr, ..]
            ? attr.FrameworkName
            : throw new InvalidOperationException("找不到 TargetFrameworkAttribute");
}
```

> Step 5b 会把本文件替换为同时暴露平台信息的版本。先按上面这个写，测试先跑通一次。

路径：`tests/PlcDataHub.Core.Tests/PlcDataHub.Core.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>PlcDataHub.Core.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="FluentAssertions" Version="6.12.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\PlcDataHub.Core\PlcDataHub.Core.csproj" />
  </ItemGroup>
</Project>
```

> 版本说明：`FluentAssertions` **必须用 6.x**——8.0 起改为商业许可，7.x 起许可证变更，6.12.1 是最后的宽松许可版本。`xunit` 用 2.x 而非 3.x，因为 3.x 的 runner 生态在 .NET 8 上仍有兼容问题。

- [ ] **Step 5: 写测试**

路径：`tests/PlcDataHub.Core.Tests/TargetFrameworkTests.cs`
（内容见 Step 5b 的最终版本；先只写 `目标框架必须是_net8_0` 一条 Fact 即可）

> **⚠️ 这条测试不会"先红后绿"，这是正常的，不要为了凑红色去故意写错配置。**
> Task 1 实测确认：断言对象是**编译时 TFM 决定**的属性，而 Step 2 的 `Directory.Build.props`
> 已经写对了框架，所以只要项目能编译，这条断言必然为真。它测试的是"构建配置的结果"，
> 不是"待实现的业务逻辑"，因此不存在"实现缺失导致失败"的阶段。
> 它的价值是**回归防护**——拦住未来的误升级。
>
> **要证明这条断言真的有拦截力，用变异测试**（Task 1 已实测有效）：
> 临时把 `Directory.Build.props` 的 `TargetFramework` 改成 `net10.0-windows`，
> 跑 `dotnet test`，应看到 `失败: 1`（断言报 `Expected .NETCoreApp,Version=v8.0 but found v10.0`），
> 然后**在 `finally` 中还原并逐字节校验**（字节数、无 U+FFFD、纯 LF、末尾换行）。
>
> 注意：**不要用 `dotnet test -p:TargetFramework=net10.0-windows` 做这个变异**——
> Task 1 实测它会让 `project.assets.json` 与 TFM 不一致，报 `error NETSDK1005`
> 在**构建阶段**就失败，证明不了断言有效。必须真改构建配置文件。

```csharp
using FluentAssertions;
using PlcDataHub.Core;
using Xunit;

namespace PlcDataHub.Core.Tests;

public class TargetFrameworkTests
{
    [Fact]
    public void 目标框架必须是_net8_0_windows()
    {
        // 规格 1.5 节：.NET 9/10 已放弃 Windows 10 支持。
        // 这个断言失败意味着有人升级了框架，会导致 Win10 工控机无法运行。
        TargetFrameworkProbe.TargetFramework.Should().Be(".NETCoreApp,Version=v8.0");
    }
}
```

> **实测值（2026-02-09 本机验证，直接照此断言，不需要再试）**：
> - `TargetFrameworkAttribute.FrameworkName` = `.NETCoreApp,Version=v8.0`（**不带** `-windows` 后缀）
> - `SupportedOSPlatformAttribute.PlatformName` = `Windows7.0`（这是 `net8.0-windows` 的 TFM 下限，不是实际系统版本）
> - 测试实际运行在 `.NET 8.0.22` 上
>
> 单看 `FrameworkName` 只能证明是 net8.0，**不能证明是 `-windows` 变体**。
> 所以再加一条 `SupportedOSPlatform == "Windows7.0"` 的断言把平台也锁住——
> 若有人把 TFM 改成 `net8.0`（去掉 `-windows`），这条会失败。
> 下面的 `FrameworkProbe` 同时暴露这两个值。

- [ ] **Step 5b: 让探针同时暴露平台信息**

路径：`src/PlcDataHub.Core/TargetFrameworkProbe.cs`（覆盖 Step 4 的版本）

```csharp
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
```

并把 `TargetFrameworkTests` 改成：

```csharp
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
```

- [ ] **Step 6: 创建解决方案并加入项目**

Run:
```powershell
cd D:\deepseek\PLC_Collect
dotnet new sln -n PlcDataHub
dotnet sln add src\PlcDataHub.Core\PlcDataHub.Core.csproj
dotnet sln add tests\PlcDataHub.Core.Tests\PlcDataHub.Core.Tests.csproj
dotnet build
```

Expected: `Build succeeded`，且输出路径含 `net8.0-windows`（例如 `src\PlcDataHub.Core\bin\Debug\net8.0-windows\`）。

- [ ] **Step 7: 运行测试**（断言值已实测确定，这一步只做验证）

Run:
```powershell
dotnet test tests\PlcDataHub.Core.Tests
```

Expected: `Passed! - Failed: 0, Passed: 2`。

若失败，按错误信息判断：
- 实际值出现 `v9.0` 或 `v10.0` → `Directory.Build.props` 没生效，**停下来先修**
- 实际值出现 `v8.0` 但平台断言失败 → 有人把 TFM 写成了裸 `net8.0`，检查 `Directory.Build.props`

- [ ] **Step 8: 创建 `.editorconfig` 并提交**

路径：`.editorconfig`

```ini
root = true

[*]
charset = utf-8
end_of_line = lf
insert_final_newline = true
trim_trailing_whitespace = true
indent_style = space
indent_size = 4

[*.{json,yml,yaml,props,targets,csproj,sln}]
indent_size = 2

[*.md]
trim_trailing_whitespace = false
```

Run:
```powershell
git add <本任务改动的显式路径>
git commit -m "chore: 工程骨架与目标框架锁定 net8.0-windows

- Directory.Build.props 统一锁定框架，防止误升级到 net9/net10
- 新增目标框架探针测试，违反约束即构建失败
- FluentAssertions 固定 6.12.1（8.0 起改商业许可）"
```

---

## Task 2：领域模型

**Files:**
- Create: `src/PlcDataHub.Core/Model/Enums.cs`
- Create: `src/PlcDataHub.Core/Model/DeviceConnection.cs`
- Create: `src/PlcDataHub.Core/Model/PollGroup.cs`
- Create: `src/PlcDataHub.Core/Model/PointConfig.cs`
- Create: `tests/PlcDataHub.Core.Tests/ModelTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `enum ProtocolKind { S7, ModbusTcp, ModbusRtu }`
  - `enum S7Area { Input, Output, Memory, DataBlock }`
  - `enum ModbusRegisterArea { Coil, DiscreteInput, HoldingRegister, InputRegister }`
  - `enum PointDataType { Bool, Byte, Word, DWord, SInt, USInt, Int, UInt, DInt, UDInt, Real, LReal, String, Dtl }`
  - `enum ByteOrder { Big, Little }`
  - `record DeviceConnection(int ConnId, string ConnCode, string ConnName, ProtocolKind Protocol, string? Host, int Port, int Rack, int Slot, string? SerialPort, int Baud, bool Enabled)`
  - `record PointConfig(int PointId, string PointCode, string PointName, string ColumnName, PointDataType DataType, ByteOrder ByteOrder, double Scale, double Offset, bool Enabled, S7Address? S7, ModbusAddress? Modbus)`
  - `record S7Address(S7Area Area, int Db, int ByteOffset, int BitOffset)`
  - `record ModbusAddress(int SlaveId, ModbusRegisterArea Area, int RegisterAddress)`
  - `record PollGroup(int GroupId, int ConnId, string GroupCode, string GroupName, int PeriodMs, string TableName, bool Enabled, IReadOnlyList<PointConfig> Points)`

- [ ] **Step 1: 写失败测试**

路径：`tests/PlcDataHub.Core.Tests/ModelTests.cs`

```csharp
using FluentAssertions;
using PlcDataHub.Core.Model;
using Xunit;

namespace PlcDataHub.Core.Tests;

public class ModelTests
{
    [Fact]
    public void 采集点可以只带_Modbus_地址而不带_S7_地址()
    {
        var point = new PointConfig(
            PointId: 1,
            PointCode: "temp_1",
            PointName: "1#温度",
            ColumnName: "p1_wen_du",
            DataType: PointDataType.Real,
            ByteOrder: ByteOrder.Big,
            Scale: 1.0,
            Offset: 0.0,
            Enabled: true,
            S7: null,
            Modbus: new ModbusAddress(SlaveId: 1, Area: ModbusRegisterArea.HoldingRegister, RegisterAddress: 100));

        point.S7.Should().BeNull();
        point.Modbus!.RegisterAddress.Should().Be(100);
    }

    [Fact]
    public void 设备连接默认端口随协议不同()
    {
        DeviceConnection.DefaultPortFor(ProtocolKind.S7).Should().Be(102);
        DeviceConnection.DefaultPortFor(ProtocolKind.ModbusTcp).Should().Be(502);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
dotnet test tests\PlcDataHub.Core.Tests --filter 采集点可以只带_Modbus_地址而不带_S7_地址
```
Expected: 编译失败，`PointConfig` / `ModbusAddress` / `DeviceConnection` 未定义。

- [ ] **Step 3: 写实现**

路径：`src/PlcDataHub.Core/Model/Enums.cs`

```csharp
namespace PlcDataHub.Core.Model;

/// <summary>设备连接使用的协议。</summary>
public enum ProtocolKind
{
    S7,
    ModbusTcp,
    ModbusRtu,
}

/// <summary>S7 地址区域。I/Q/M/DB 四种，对应规格 3.2 节 s7_area 字段。</summary>
public enum S7Area
{
    /// <summary>输入区 I</summary>
    Input,

    /// <summary>输出区 Q</summary>
    Output,

    /// <summary>标志位区 M</summary>
    Memory,

    /// <summary>数据块 DB。S7-200 SMART 的 V 区按 DB1 处理。</summary>
    DataBlock,
}

/// <summary>Modbus 寄存器区。对应功能码 01/02/03/04。</summary>
public enum ModbusRegisterArea
{
    /// <summary>线圈，功能码 01，可读可写</summary>
    Coil,

    /// <summary>离散输入，功能码 02，只读</summary>
    DiscreteInput,

    /// <summary>保持寄存器，功能码 03，可读可写</summary>
    HoldingRegister,

    /// <summary>输入寄存器，功能码 04，只读</summary>
    InputRegister,
}

/// <summary>采集点的原始数据类型，对应规格 3.2 节 data_type 字段。</summary>
public enum PointDataType
{
    Bool,
    Byte,
    Word,
    DWord,
    SInt,
    USInt,
    Int,
    UInt,
    DInt,
    UDInt,
    Real,
    LReal,
    String,
    Dtl,
}

/// <summary>多字节类型的字节序。</summary>
public enum ByteOrder
{
    /// <summary>大端（西门子默认）</summary>
    Big,

    /// <summary>小端（部分 Modbus 设备）</summary>
    Little,
}
```

路径：`src/PlcDataHub.Core/Model/DeviceConnection.cs`

```csharp
namespace PlcDataHub.Core.Model;

/// <summary>
/// 设备连接。规格 3.2 节 cfg.device_connection 的内存表示。
/// 只承载数据，不含行为。
/// </summary>
/// <param name="ConnId">连接主键</param>
/// <param name="ConnCode">设备代码，仅 [a-z0-9_]，用于生成表名</param>
/// <param name="ConnName">显示名，可中文</param>
/// <param name="Protocol">协议类型</param>
/// <param name="Host">IP 或主机名。S7 / Modbus TCP 必填</param>
/// <param name="Port">端口</param>
/// <param name="Rack">S7 机架号</param>
/// <param name="Slot">S7 槽号</param>
/// <param name="SerialPort">Modbus RTU 串口名，如 COM3</param>
/// <param name="Baud">Modbus RTU 波特率</param>
/// <param name="Enabled">是否启用</param>
public sealed record DeviceConnection(
    int ConnId,
    string ConnCode,
    string ConnName,
    ProtocolKind Protocol,
    string? Host,
    int Port,
    int Rack,
    int Slot,
    string? SerialPort,
    int Baud,
    bool Enabled)
{
    /// <summary>返回指定协议的默认端口。S7 为 102，Modbus TCP 为 502。</summary>
    public static int DefaultPortFor(ProtocolKind protocol) => protocol switch
    {
        ProtocolKind.S7 => 102,
        ProtocolKind.ModbusTcp => 502,
        ProtocolKind.ModbusRtu => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "未知协议"),
    };
}
```

路径：`src/PlcDataHub.Core/Model/PointConfig.cs`

```csharp
namespace PlcDataHub.Core.Model;

/// <summary>S7 地址。规格 3.2 节 s7_area / s7_db / s7_byte / s7_bit。</summary>
/// <param name="Area">区域 I/Q/M/DB</param>
/// <param name="Db">DB 号。S7-200 SMART 的 V 区 = DB1</param>
/// <param name="ByteOffset">字节偏移</param>
/// <param name="BitOffset">位偏移 0~7，仅 BOOL 使用</param>
public sealed record S7Address(S7Area Area, int Db, int ByteOffset, int BitOffset);

/// <summary>Modbus 地址。规格 3.2 节 mb_slave / mb_reg_area / mb_address。</summary>
/// <param name="SlaveId">从站号 1~247</param>
/// <param name="Area">寄存器区</param>
/// <param name="RegisterAddress">寄存器地址，0 基</param>
public sealed record ModbusAddress(int SlaveId, ModbusRegisterArea Area, int RegisterAddress);

/// <summary>
/// 采集点。规格 3.2 节 cfg.point 的内存表示。
/// S7 与 Modbus 地址字段二者只会有其一非空，取决于所属连接的协议。
/// </summary>
public sealed record PointConfig(
    int PointId,
    string PointCode,
    string PointName,
    string ColumnName,
    PointDataType DataType,
    ByteOrder ByteOrder,
    double Scale,
    double Offset,
    bool Enabled,
    S7Address? S7,
    ModbusAddress? Modbus)
{
    /// <summary>
    /// 把原始值转换为工程值：raw * Scale + Offset。
    /// 规格 3.4 节：落库前应用，库里存工程值。
    /// </summary>
    public double ToEngineeringValue(double raw) => raw * Scale + Offset;
}
```

路径：`src/PlcDataHub.Core/Model/PollGroup.cs`

```csharp
namespace PlcDataHub.Core.Model;

/// <summary>
/// 采集组。规格 3.2 节 cfg.poll_group 的内存表示。
/// 一组对应数据库中一张宽表。
/// </summary>
/// <param name="GroupId">组主键</param>
/// <param name="ConnId">所属连接</param>
/// <param name="GroupCode">组代码，仅 [a-z0-9_]</param>
/// <param name="GroupName">显示名，可中文</param>
/// <param name="PeriodMs">采集周期（毫秒），1000 ~ 600000</param>
/// <param name="TableName">目标表名（不含 schema），位于 schema d 下</param>
/// <param name="Enabled">是否启用</param>
/// <param name="Points">组内采集点</param>
public sealed record PollGroup(
    int GroupId,
    int ConnId,
    string GroupCode,
    string GroupName,
    int PeriodMs,
    string TableName,
    bool Enabled,
    IReadOnlyList<PointConfig> Points);
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```powershell
dotnet test tests\PlcDataHub.Core.Tests
```
Expected: `Passed! - Failed: 0, Passed: 4`（含 Task 1 的 2 条框架断言：Task 1 遗留 2 + 本任务新增 2 = 4。原文写 3 是漏算了 Task 1 Step 5b 把框架测试由 1 条扩成 2 条。）

- [ ] **Step 5: 提交**

```powershell
git add <本任务改动的显式路径>
git commit -m "feat: 领域模型（连接/采集组/采集点/协议枚举）"
```

---

## Task 3：列名生成器

对应规格 3.5 节。**这是最容易被低估的一块**：PostgreSQL 标识符上限是 63 **字节**而非字符，中文列名会直接踩爆。

**Files:**
- Create: `src/PlcDataHub.Core/Naming/ColumnNameGenerator.cs`
- Create: `tests/PlcDataHub.Core.Tests/ColumnNameGeneratorTests.cs`
- Modify: `src/PlcDataHub.Core/PlcDataHub.Core.csproj`（加拼音包引用）

**Interfaces:**
- Consumes: 无
- Produces:
  - `static class ColumnNameGenerator`
  - `static string Normalize(string displayName)` —— 单个名字规范化，**不做冲突处理**
  - `static IReadOnlyList<string> AssignUniqueColumns(IEnumerable<string> displayNames)` —— 批量分配，处理组内重名
  - `const int MaxIdentifierBytes = 63`

- [ ] **Step 1: 写失败测试**

路径：`tests/PlcDataHub.Core.Tests/ColumnNameGeneratorTests.cs`

```csharp
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
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
dotnet test tests\PlcDataHub.Core.Tests --filter ColumnNameGeneratorTests
```
Expected: 编译失败，`ColumnNameGenerator` 未定义。

- [ ] **Step 3: 加拼音包引用并验证可用**

> **⚠️ 控制者扫描发现的两点，必须按序执行：**
>
> **① 拼音库的 API 是未知量，必须实测，不许照抄。** 下面的实现代码里写的是
> `ToolGood.Words.WordsHelper.GetPinyin(string)` —— 这是**未经核实的猜测**（规格 8.1 节已把拼音库列为待验证依赖）。
> 本步骤的正确顺序是：**先加包 → 写一个最小调用样例跑通 → 按实际 API 调整实现**，
> 而不是先照抄实现再去调测试预期。
>
> **② 本步骤应在写测试之前完成。** if 包还原失败，会走降级链，而**降级链要求改写
> `ColumnNameGeneratorTests` 中带拼音的两条 `InlineData`**。若先写了测试再发现要降级，
> 会白改一轮。故顺序为：Step 3（验证依赖）→ Step 1（写测试）→ Step 2（确认 RED）→ Step 4（实现）。

修改 `src/PlcDataHub.Core/PlcDataHub.Core.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>PlcDataHub.Core</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <!-- 首选拼音库。若此包还原失败，见 Step 3b 的降级方案。 -->
    <PackageReference Include="ToolGood.Words.Pinyin" Version="3.0.1.4" />
  </ItemGroup>
</Project>
```

> **⚠️ 包名已于 Task 3 实测后修正（原写 `ToolGood.Words.FirstPinyin` 3.0.1.3，是错的）。**
>
> 实测结论：`FirstPinyin` 是**拼音首字母**库，`GetFirstPinyin("温度")` 返回 `"WD"`，
> **语义上产不出规格 3.5 节要求的 `wen_du`**；且它没有 `GetPinyin` 方法，原示例代码连编译都过不了。
> 正确选择是同作者的**全拼**包 `ToolGood.Words.Pinyin`。
>
> 权威依据见 `docs/specs/2026-02-09-plc-datahub-design.md` §8.1「选型结论」与 §8.2 依赖清单
> （含实测 API 形状：`GetPinyin(string text, string splitSpan, bool tone)`、tone 必填、
> 返回首字母大写故须 `ToLowerInvariant`、`splitSpan` 是字符之间连接符故须整段转换、
> 库对码表外汉字段内透传故须逐字符过滤 ASCII）。
> **不要"照原样修正"回 FirstPinyin** —— 那会静默打破规格 3.5 节，且现有测试会失败。

Run:
```powershell
dotnet restore src\PlcDataHub.Core
```
Expected: `Restored`。若报找不到包或目标框架不兼容，执行 **Step 3b**。

**然后写一个最小样例实测 API 形状**（例如在测试项目里临时加一条调用，或用 `dotnet run` 的临时控制台，
用 `csc` 不可行时的替代做法写在报告里），确认：
- 方法名与重载（`WordsHelper.GetPinyin` 是否存在？参数是 `string` 还是 `char`？有无 `separator` 参数？）
- 返回是否**无声调**（`wēn dù` 带声调会导致 `温度 → wēn_dù` 而不是 `wen_du`）
- 多音字与单个汉字的行为

**把实测结果写进报告**，并按实际 API 调整 Step 4 的实现与 Step 1 的测试预期。

- [ ] **Step 3b: 拼音库降级（仅当 Step 3 失败时执行）**

按规格 8.1 节的降级链，把 csproj 里的包引用整段删除（不引入任何拼音库），
并让 `ColumnNameGenerator` 走"无拼音库"分支：中文字符整体丢弃，
若结果为 `p{pointId}` 形式则由调用方补位。

此时必须**在 `ColumnNameGeneratorTests` 中移除**带中文的两条 `InlineData`
（`("温度", "wen_du")` 与 `("1#窑尾温度", "p1_yao_wei_wen_du")`），
并用 `[Fact(Skip = "拼音库不可用，走降级链")]` 单独记录这两条预期，其余用例保持通过。

> **注意**：不能直接给 `[Theory]` 加 `Skip` —— 那会跳过整个 Theory（含 4 条 InlineData 中
> 与中文无关的 `Temp Kiln`、`PID_输出%`）。原 brief 此处写法有误，已修正为"移除这两条 InlineData"。

**这一步的目的是保证依赖不可用时项目仍能继续推进，而不是卡住。**

- [ ] **Step 4: 写实现**

路径：`src/PlcDataHub.Core/Naming/ColumnNameGenerator.cs`

```csharp
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

        foreach (var ch in displayName)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (IsChinese(ch))
            {
                sb.Append(ToPinyin(ch));
            }
            else
            {
                // 空格、#、%、-、. 等一律折叠为下划线，后续统一收敛连续下划线
                sb.Append('_');
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

    private static string ToPinyin(char ch)
    {
        // 实测 API（ToolGood.Words.Pinyin 3.0.1.4）：
        //   GetPinyin(string text, string splitSpan, bool tone) —— tone 必填，无单参重载；
        //   tone:false → "Wen_Du"（无声调，首字母大写，故须 ToLowerInvariant）；
        //   splitSpan 是"字符之间"的连接符，单字调用不含分隔符，故必须整段转换。
        // ⚠️ 不要换成 ToolGood.Words.FirstPinyin：那是首字母库，返回 "WD" 而非 "wen_du"。
        var pinyin = ToolGood.Words.Pinyin.WordsHelper.GetPinyin(chineseRun, "_", false);

        if (string.IsNullOrEmpty(pinyin))
        {
            return string.Empty;
        }

        // 库对码表外的汉字是"段内逐个透传"（GetPinyin("温鿿") == "Wen_鿿"），
        // 故不能靠"整段是否等于输入"判断转出成功 —— 那会漏掉部分转出的情形。
        // 逐字符只保留 ASCII 字母数字，从根上保证列名是纯 ASCII。
        var sb = new StringBuilder(pinyin.Length);

        foreach (var ch in pinyin.ToLowerInvariant())
        {
            sb.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' ? ch : '_');
        }

        return sb.ToString();
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
        var prefix = TruncateToBytes(value, keepBytes);

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
```

- [ ] **Step 5: 运行测试确认通过**

Run:
```powershell
dotnet test tests\PlcDataHub.Core.Tests --filter ColumnNameGeneratorTests
```
Expected: `Passed!`。特别注意 `PID_输出% → pid_shu_chu` 这一条——它会验证 `%` 被折叠、连续下划线被收敛、末尾下划线被去掉。若实际结果与预期不符（例如拼音库返回带声调的拼音），**以实际库行为为准修正测试预期，并在此处记录差异**。

- [ ] **Step 6: 提交**

```powershell
git add <本任务改动的显式路径>
git commit -m "feat: 列名生成器（中文转拼音、ASCII 规范化、冲突避让、63 字节截断）"
```

---

## Task 4：SQL 类型映射与期望结构构建

对应规格 3.4 节的类型映射表。

**Files:**
- Create: `src/PlcDataHub.Core/Schema/SqlTypeMapper.cs`
- Create: `src/PlcDataHub.Core/Model/DesiredSchema.cs`
- Create: `src/PlcDataHub.Core/Schema/DesiredSchemaBuilder.cs`
- Create: `tests/PlcDataHub.Core.Tests/DesiredSchemaBuilderTests.cs`

**Interfaces:**
- Consumes: `PointDataType`（Task 2）、`ColumnNameGenerator`（Task 3）
- Produces:
  - `static class SqlTypeMapper { static string ToPostgresType(PointDataType type); }`
  - `record DesiredColumn(string Name, string PostgresType, bool IsNullable)`
  - `record DesiredTable(string TableName, IReadOnlyList<DesiredColumn> Columns)`
  - `record DesiredSchema(IReadOnlyList<DesiredTable> Tables)`
  - `static class DesiredSchemaBuilder { static DesiredSchema Build(IEnumerable<DeviceConnection> connections, IEnumerable<PollGroup> groups); }`

- [ ] **Step 1: 写失败测试**

路径：`tests/PlcDataHub.Core.Tests/DesiredSchemaBuilderTests.cs`

```csharp
using FluentAssertions;
using PlcDataHub.Core.Model;
using PlcDataHub.Core.Schema;
using Xunit;

namespace PlcDataHub.Core.Tests;

public class DesiredSchemaBuilderTests
{
    [Theory]
    [InlineData(PointDataType.Bool, "boolean")]
    [InlineData(PointDataType.Byte, "integer")]
    [InlineData(PointDataType.Word, "integer")]
    [InlineData(PointDataType.DWord, "integer")]
    [InlineData(PointDataType.Int, "integer")]
    [InlineData(PointDataType.DInt, "integer")]
    [InlineData(PointDataType.Real, "double precision")]
    [InlineData(PointDataType.LReal, "double precision")]
    [InlineData(PointDataType.String, "text")]
    [InlineData(PointDataType.Dtl, "timestamp")]
    public void 类型映射符合规格_3_4_节(PointDataType type, string expected)
    {
        SqlTypeMapper.ToPostgresType(type).Should().Be(expected);
    }

    [Fact]
    public void 每张表固定包含时间列与质量列()
    {
        var schema = DesiredSchemaBuilder.Build(
            new[] { MakeConnection() },
            new[] { MakeGroup(new[] { MakePoint(1, "温度", "wen_du") }) });

        var table = schema.Tables.Should().ContainSingle().Subject;

        table.TableName.Should().Be("plc01_fast");
        table.Columns[0].Name.Should().Be("ts");
        table.Columns[0].PostgresType.Should().Be("timestamp");
        table.Columns[0].IsNullable.Should().BeFalse();

        table.Columns[1].Name.Should().Be("q");
        table.Columns[1].PostgresType.Should().Be("smallint");
        table.Columns[1].IsNullable.Should().BeFalse();

        table.Columns[2].Name.Should().Be("src_ts");
        table.Columns[2].PostgresType.Should().Be("timestamp");
        table.Columns[2].IsNullable.Should().BeTrue();
    }

    [Fact]
    public void 停用的采集点不进表结构()
    {
        var schema = DesiredSchemaBuilder.Build(
            new[] { MakeConnection() },
            new[] { MakeGroup(new[] { MakePoint(1, "温度", "wen_du"), MakePoint(2, "停用点", "ting_yong", enabled: false) }) });

        schema.Tables.Single().Columns.Select(c => c.Name)
            .Should().NotContain("ting_yong");
    }

    [Fact]
    public void 停用的采集组不生成表()
    {
        var disabledGroup = MakeGroup(new[] { MakePoint(1, "温度", "wen_du") }) with { Enabled = false };

        var schema = DesiredSchemaBuilder.Build(new[] { MakeConnection() }, new[] { disabledGroup });

        schema.Tables.Should().BeEmpty();
    }

    [Fact]
    public void 停用的连接下所有组都不生成表()
    {
        var connection = MakeConnection() with { Enabled = false };

        var schema = DesiredSchemaBuilder.Build(
            new[] { connection },
            new[] { MakeGroup(new[] { MakePoint(1, "温度", "wen_du") }) });

        schema.Tables.Should().BeEmpty();
    }

    [Fact]
    public void 列名为空时用采集点显示名自动生成()
    {
        var schema = DesiredSchemaBuilder.Build(
            new[] { MakeConnection() },
            new[] { MakeGroup(new[] { MakePoint(1, "窑尾温度", columnName: "") }) });

        schema.Tables.Single().Columns.Select(c => c.Name).Should().Contain("yao_wei_wen_du");
    }

    [Fact]
    public void 组内重名的自动生成列名会被避让()
    {
        var schema = DesiredSchemaBuilder.Build(
            new[] { MakeConnection() },
            new[] { MakeGroup(new[] { MakePoint(1, "温度", ""), MakePoint(2, "温度", "") }) });

        var names = schema.Tables.Single().Columns.Select(c => c.Name).ToList();
        names.Should().Contain("wen_du");
        names.Should().Contain("wen_du_2");
    }

    private static DeviceConnection MakeConnection() => new(
        ConnId: 1, ConnCode: "plc01", ConnName: "1#窑 PLC", Protocol: ProtocolKind.ModbusTcp,
        Host: "192.168.0.10", Port: 502, Rack: 0, Slot: 1, SerialPort: null, Baud: 0, Enabled: true);

    private static PollGroup MakeGroup(IReadOnlyList<PointConfig> points) => new(
        GroupId: 1, ConnId: 1, GroupCode: "fast", GroupName: "快组",
        PeriodMs: 1000, TableName: "plc01_fast", Enabled: true, Points: points);

    private static PointConfig MakePoint(int id, string displayName, string columnName, bool enabled = true) => new(
        PointId: id, PointCode: $"p{id}", PointName: displayName, ColumnName: columnName,
        DataType: PointDataType.Real, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0,
        Enabled: enabled, S7: null,
        Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100 + id));
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
dotnet test tests\PlcDataHub.Core.Tests --filter DesiredSchemaBuilderTests
```
Expected: 编译失败，`SqlTypeMapper` / `DesiredSchemaBuilder` 未定义。

- [ ] **Step 3: 写实现**

路径：`src/PlcDataHub.Core/Schema/SqlTypeMapper.cs`

```csharp
using PlcDataHub.Core.Model;

namespace PlcDataHub.Core.Schema;

/// <summary>
/// 采集点数据类型 → PostgreSQL 类型。规格 3.4 节类型映射表。
/// 刻意不做"看起来更干净"的转换：Real 到 PG 就是 double precision，精度不丢。
/// </summary>
public static class SqlTypeMapper
{
    public static string ToPostgresType(PointDataType type) => type switch
    {
        PointDataType.Bool => "boolean",
        PointDataType.Byte => "integer",
        PointDataType.Word => "integer",
        PointDataType.DWord => "integer",
        PointDataType.SInt => "integer",
        PointDataType.USInt => "integer",
        PointDataType.Int => "integer",
        PointDataType.UInt => "integer",
        PointDataType.DInt => "integer",
        PointDataType.UDInt => "integer",
        PointDataType.Real => "double precision",
        PointDataType.LReal => "double precision",
        PointDataType.String => "text",
        PointDataType.Dtl => "timestamp",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知数据类型"),
    };
}
```

路径：`src/PlcDataHub.Core/Model/DesiredSchema.cs`

```csharp
namespace PlcDataHub.Core.Model;

/// <summary>期望表中的一列。</summary>
/// <param name="Name">列名（ASCII）</param>
/// <param name="PostgresType">PostgreSQL 类型</param>
/// <param name="IsNullable">是否允许 NULL</param>
public sealed record DesiredColumn(string Name, string PostgresType, bool IsNullable);

/// <summary>期望的表结构。TableName 不含 schema，固定位于 schema d 下。</summary>
public sealed record DesiredTable(string TableName, IReadOnlyList<DesiredColumn> Columns);

/// <summary>由配置推算出的全部期望结构。</summary>
public sealed record DesiredSchema(IReadOnlyList<DesiredTable> Tables)
{
    /// <summary>固定列：时间戳 ts。</summary>
    public const string TimestampColumn = "ts";

    /// <summary>固定列：行质量 q。0=好 1=部分坏点 2=补写。</summary>
    public const string QualityColumn = "q";

    /// <summary>固定列：原始采集时刻 src_ts，仅补写行非空。</summary>
    public const string SourceTimestampColumn = "src_ts";
}
```

路径：`src/PlcDataHub.Core/Schema/DesiredSchemaBuilder.cs`

```csharp
using PlcDataHub.Core.Model;
using PlcDataHub.Core.Naming;

namespace PlcDataHub.Core.Schema;

/// <summary>
/// 配置图 → 期望结构。规格 3.4 节。
/// 规则：停用的连接 / 组 / 点一律不进期望结构（不生成表、不生成列）。
/// </summary>
public static class DesiredSchemaBuilder
{
    public static DesiredSchema Build(
        IEnumerable<DeviceConnection> connections,
        IEnumerable<PollGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(groups);

        var enabledConnIds = connections
            .Where(c => c.Enabled)
            .Select(c => c.ConnId)
            .ToHashSet();

        var tables = new List<DesiredTable>();

        foreach (var group in groups.Where(g => g.Enabled && enabledConnIds.Contains(g.ConnId)))
        {
            tables.Add(BuildTable(group));
        }

        return new DesiredSchema(tables);
    }

    private static DesiredTable BuildTable(PollGroup group)
    {
        var enabledPoints = group.Points.Where(p => p.Enabled).ToList();

        var columns = new List<DesiredColumn>
        {
            // 固定三列，顺序固定：ts / q / src_ts
            new(DesiredSchema.TimestampColumn, "timestamp", IsNullable: false),
            new(DesiredSchema.QualityColumn, "smallint", IsNullable: false),
            new(DesiredSchema.SourceTimestampColumn, "timestamp", IsNullable: true),
        };

        // 点为 NULL 是常态（坏点写 NULL），所以数据列一律允许 NULL
        foreach (var (point, columnName) in AssignColumns(enabledPoints))
        {
            columns.Add(new DesiredColumn(
                columnName,
                SqlTypeMapper.ToPostgresType(point.DataType),
                IsNullable: true));
        }

        return new DesiredTable(group.TableName, columns);
    }

    /// <summary>
    /// 决定每个点最终的列名：优先用手工指定的 column_name；
    /// 为空的名由 ColumnNameGenerator 生成；整组重名统一避让由生成器负责。
    /// </summary>
    private static IEnumerable<(PointConfig Point, string ColumnName)> AssignColumns(
        IReadOnlyList<PointConfig> points)
    {
        // 先用手工列名占位，再为剩余的自动命名，避免自动名抢占手工名
        var manual = points
            .Select((p, index) => (Point: p, Index: index))
            .Where(x => !string.IsNullOrWhiteSpace(x.Point.ColumnName))
            .ToList();

        var reserved = manual
            .Select(x => x.Point.ColumnName)
            .ToHashSet(StringComparer.Ordinal);

        var assigned = new string?[points.Count];

        foreach (var (point, index) in manual)
        {
            assigned[index] = point.ColumnName;
        }

        var autoNeeded = points
            .Select((p, index) => (Point: p, Index: index))
            .Where(x => string.IsNullOrWhiteSpace(x.Point.ColumnName))
            .ToList();

        if (autoNeeded.Count > 0)
        {
            var generated = ColumnNameGenerator.AssignUniqueColumns(
                autoNeeded.Select(x => x.Point.PointName));

            var cursor = 0;
            foreach (var (_, index) in autoNeeded)
            {
                var candidate = generated[cursor++];
                var suffix = 2;

                while (reserved.Contains(candidate))
                {
                    candidate = $"{generated[cursor - 1]}_{suffix++}";
                }

                reserved.Add(candidate);
                assigned[index] = candidate;
            }
        }

        for (var i = 0; i < points.Count; i++)
        {
            yield return (points[i], assigned[i]!);
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```powershell
dotnet test tests\PlcDataHub.Core.Tests --filter DesiredSchemaBuilderTests
```
Expected: `Passed!`

- [ ] **Step 5: 提交**

```powershell
git add <本任务改动的显式路径>
git commit -m "feat: SQL 类型映射与期望结构构建（配置图 → 目标表结构）"
```

---

## Task 5：迁移差异引擎

对应规格 4.1 / 4.2 节。**这是"绝不静默丢数据"原则的落点**，也是本项目逻辑最密的一块。

### Task 5 必须满足的跨任务约束（由 Task 1~4 的实际产出推导，非可选）

> 以下四条是 Task 1~4 执行过程中产生的**接口约束**。它们不是"锦上添花"，若不满足会直接导致错误或破坏验收项。

1. **列名比对必须大小写不敏感（`OrdinalIgnoreCase`）。**
   理由：PostgreSQL 未加引号的标识符大小写不敏感。若用 `Ordinal` 拿期望结构与数据库实际结构比对，遇到库里已有 `Wen_Du` 而期望是 `wen_du` 时会**误判为"缺列"**，进而生成 `ADD COLUMN`，而该列实际已存在 → 建表/迁移失败。
   （Task 4 已在**期望结构内部**加了大小写去重守卫，但那只覆盖"期望 vs 期望"，覆盖不了"期望 vs 数据库实际"。）

2. **不得按 `information_schema.columns.ordinal_position` 做列顺序比对。**
   理由：规格 4.3 节要求迁移**幂等**（同一配置连跑两次，第二次不产生任何 DDL，验收项 A12）。而目标列序是 `ts / q / src_ts / 数据列…`，与规格 3.4 节示例 DDL 把 `src_ts` 放最后**不同**；且 PostgreSQL 的物理列序会随 `ADD COLUMN` 累积而偏离期望序。按位置比对会让"列序不同但集合相同"被反复判为"需迁移"，破坏幂等。
   **正确做法：按列名集合比对，不比对顺序。**

3. **`q` 列的 `default 0` 必须由 Task 5 的建表 SQL 硬编码。**
   理由：规格 3.4 节要求 `q smallint not null default 0`，但 `DesiredColumn` 只表达 `(Name, PostgresType, IsNullable)`，**无法表达默认值**。Task 4 的实现已确认这一保真差异，故由建表 SQL 侧补上，不要指望 `DesiredColumn` 里有。

4. **手工列名的三项校验中，有两项归本任务。**
   Task 4 的 `DesiredSchemaBuilder.GuardColumns` **已拦**：列名重复（含仅大小写不同）、列名超 63 字节（手工名与自动名一视同仁）。
   **本任务必须补**：
   - **手工名含非 ASCII** → 拒绝（Task 4 不做，因为手工名被原样赋值、不经 `ColumnNameGenerator.Normalize` 净化）
   - **手工名含内嵌引号 / 需转义字符** → 拒绝或正确转义（这是 **SQL 注入面**，`README-VALIDATION.md` 专门警告过）
   > 不要**重复实现**长度与重复校验——Task 4 已覆盖，重复实现会形成两处漂移。

5. **表名必须加引号（`Qualified` / `IndexName`），并为期望表名加 `[A-Za-z0-9_]` 白名单。**
   **两道门都要，不可互相替代**（Task 5 的变异证据：撤掉引号红 28 条；只留引号则 `;`/空格/括号类表名仍精确红 2 条）。
   理由：列名全程有引号包裹，故"安全 ASCII 放行"对列名成立；但**表名没有引号时同一结论不成立** —— 实测注入载荷 `x (dummy int); drop table d.other; create table d.x` 会生成可执行的多语句批。
   `existing` 侧表名**不做**白名单（第三方输入，靠引号保证安全），但仍应拦引号/反斜杠/控制字符/非 ASCII。

### ⚠️ Task 5 已知缺口（Plan 1 范围内无法解决，不得算作合规）

**规格 4.2 要求"修改列名 → `ALTER TABLE RENAME COLUMN`，数据不丢"（规格 `:368`），而 Plan 1 实现不了。**

原因：`MigrationPlanner` **没有任何字段能表达"这个点就是原来那个点、只是改了列名"**。它只能把改名的列看成"配置里没有的列"（→ 软删除保留）+ "新出现的列"（→ `ADD COLUMN`）。
唯一能承载该身份的是 `cfg.point.point_id`（配置持久化的行标识），而那要到 **Plan 2 的配置读取层**才存在。

**当前行为：安全但不符合规格。** 数据不丢（旧列被软删除保留），但新旧列不连续、历史曲线断开。
`MigrationStepKind.RenameColumn` / `RebuildColumn` 因此**无生产者**——这是**接口缺字段**，不是遗漏。

**Plan 2 必须处理**：`cfg.point` 提供 `point_id → column_name` 的历史映射后，改列名才能识别为 `RENAME` 而非"删+加"。

**Files:**
- Create: `src/PlcDataHub.Core/Migration/ColumnDefinition.cs`
- Create: `src/PlcDataHub.Core/Migration/MigrationStep.cs`
- Create: `src/PlcDataHub.Core/Migration/MigrationPlan.cs`
- Create: `src/PlcDataHub.Core/Migration/MigrationPlanner.cs`
- Create: `tests/PlcDataHub.Core.Tests/MigrationPlannerTests.cs`

**Interfaces:**
- Consumes: `DesiredSchema` / `DesiredTable` / `DesiredColumn`（Task 4）
- Produces:
  - `record ExistingColumn(string Name, string PostgresType)`
  - `record ExistingTable(string TableName, IReadOnlyList<ExistingColumn> Columns, long RowCount)`
  - `enum MigrationStepKind { CreateTable, AddColumn, RenameColumn, SoftDeleteColumn, CreateIndex, DropColumn, DropTable }`
  - `record MigrationStep(MigrationStepKind Kind, string TableName, string Sql, bool IsDestructive, string Description)`
  - `record MigrationPlan(IReadOnlyList<MigrationStep> Steps) { bool IsEmpty; IReadOnlyList<MigrationStep> DestructiveSteps; }`
  - `static class MigrationPlanner`
  - `static MigrationPlan Plan(DesiredSchema desired, IReadOnlyList<ExistingTable> existing)`
  - `static MigrationPlan PlanWithOptions(DesiredSchema desired, IReadOnlyList<ExistingTable> existing, MigrationOptions options)`
  - `record MigrationOptions(bool SoftDeleteRemovedColumns = true, bool DropRemovedTables = false)`

- [ ] **Step 1: 写失败测试**

路径：`tests/PlcDataHub.Core.Tests/MigrationPlannerTests.cs`

```csharp
using FluentAssertions;
using PlcDataHub.Core.Migration;
using PlcDataHub.Core.Model;
using Xunit;

namespace PlcDataHub.Core.Tests;

public class MigrationPlannerTests
{
    private static readonly MigrationOptions SafeOptions = new();

    [Fact]
    public void 表不存在时生成建表语句()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));

        var plan = MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions);

        plan.IsEmpty.Should().BeFalse();
        plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.CreateTable);
        plan.Steps.Single(s => s.Kind == MigrationStepKind.CreateTable).Sql
            .Should().Contain("create table if not exists d.plc01_fast")
            .And.Contain("\"ts\" timestamp not null")
            .And.Contain("\"wen_du\" double precision");
        plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.CreateIndex);
    }

    [Fact]
    public void 新增采集点生成_ADD_COLUMN()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"), ("ya_li", "double precision"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.AddColumn);
        plan.Steps.Single(s => s.Kind == MigrationStepKind.AddColumn).Sql
            .Should().Be("alter table d.plc01_fast add column if not exists \"ya_li\" double precision");
        plan.DestructiveSteps.Should().BeEmpty();
    }

    [Fact]
    public void 删除采集点默认软删除而不是_DROP()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"), ("jiu_dian", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.SoftDeleteColumn);
        plan.Steps.Should().NotContain(s => s.Kind == MigrationStepKind.DropColumn);
        plan.Steps.Single(s => s.Kind == MigrationStepKind.SoftDeleteColumn).Sql
            .Should().StartWith("alter table d.plc01_fast rename column \"jiu_dian\" to \"deleted_jiu_dian_");
    }

    [Fact]
    public void 关闭软删除时才生成_DROP_COLUMN_且标记为破坏性()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("jiu_dian", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, new MigrationOptions(SoftDeleteRemovedColumns: false));

        var drop = plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.DropColumn).Subject;
        drop.IsDestructive.Should().BeTrue();
        drop.Sql.Should().Be("alter table d.plc01_fast drop column \"jiu_dian\"");
        drop.Description.Should().Contain("丢弃");   // 破坏性操作必须写清后果
    }

    [Fact]
    public void 类型冲突阻止迁移而不是自动改类型()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "boolean"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));

        var act = () => MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        act.Should().Throw<MigrationConflictException>()
            .WithMessage("*wen_du*")
            .WithMessage("*double precision*")
            .WithMessage("*boolean*");
    }

    [Fact]
    public void 类型等价写法不算冲突()
    {
        // PG 的 information_schema 可能把 integer 报成 int4，把 timestamp 报成 timestamp without time zone
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("count", "integer"));
        var existing = Existing("plc01_fast", ("ts", "timestamp without time zone"), ("q", "int2"), ("count", "int4"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.Steps.Should().NotContain(s => s.Kind == MigrationStepKind.AddColumn);
        plan.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void 幂等_同一配置第二次为空计划()
    {
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("wen_du", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.IsEmpty.Should().BeTrue();
        plan.Steps.Should().BeEmpty();
    }

    [Fact]
    public void 删除采集组默认不删表()
    {
        var desired = new DesiredSchema(Array.Empty<DesiredTable>());
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.Steps.Should().NotContain(s => s.Kind == MigrationStepKind.DropTable);
    }

    [Fact]
    public void 关闭保留选项时才删表且标记破坏性()
    {
        var desired = new DesiredSchema(Array.Empty<DesiredTable>());
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), RowCount: 86400);

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, new MigrationOptions(SoftDeleteRemovedColumns: true, DropRemovedTables: true));

        var drop = plan.Steps.Should().ContainSingle(s => s.Kind == MigrationStepKind.DropTable).Subject;
        drop.IsDestructive.Should().BeTrue();
        drop.Description.Should().Contain("86400");   // 必须报出会丢多少行
    }

    [Fact]
    public void 软删除列名不会超过_63_字节()
    {
        var longColumn = new string('c', 55);
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), (longColumn, "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        var sql = plan.Steps.Single(s => s.Kind == MigrationStepKind.SoftDeleteColumn).Sql;
        var newName = sql[(sql.IndexOf(" to \"", StringComparison.Ordinal) + 5)..].TrimEnd('"');
        System.Text.Encoding.UTF8.GetByteCount(newName).Should().BeLessOrEqualTo(63);
    }

    private static DesiredSchema Table(string name, params (string Name, string Type)[] columns) =>
        new(new[]
        {
            new DesiredTable(name, columns
                .Select(c => new DesiredColumn(c.Name, c.Type, IsNullable: !c.Name.Equals("ts") && !c.Name.Equals("q")))
                .ToList()),
        });

    private static ExistingTable Existing(string name, params (string Name, string Type)[] columns) =>
        Existing(name, columns, RowCount: 0);

    private static ExistingTable Existing(string name, (string Name, string Type)[] columns, long RowCount) =>
        new(name, columns.Select(c => new ExistingColumn(c.Name, c.Type)).ToList(), RowCount);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
dotnet test tests\PlcDataHub.Core.Tests --filter MigrationPlannerTests
```
Expected: 编译失败，`MigrationPlanner` 等类型未定义。

- [ ] **Step 3: 写实现**

路径：`src/PlcDataHub.Core/Migration/ColumnDefinition.cs`

```csharp
namespace PlcDataHub.Core.Migration;

/// <summary>数据库中现存的列。类型取 information_schema.columns.data_type 的原始值。</summary>
public sealed record ExistingColumn(string Name, string PostgresType);

/// <summary>数据库中现存的表。</summary>
/// <param name="TableName">不含 schema 的表名</param>
/// <param name="Columns">现存列</param>
/// <param name="RowCount">行数，用于在破坏性操作提示里报出会丢多少行</param>
public sealed record ExistingTable(string TableName, IReadOnlyList<ExistingColumn> Columns, long RowCount);
```

路径：`src/PlcDataHub.Core/Migration/MigrationStep.cs`

```csharp
namespace PlcDataHub.Core.Migration;

/// <summary>迁移动作的种类。</summary>
public enum MigrationStepKind
{
    CreateTable,
    CreateIndex,
    AddColumn,
    RenameColumn,
    SoftDeleteColumn,
    RebuildColumn,
    DropColumn,
    DropTable,
}

/// <summary>
/// 一条迁移动作。Sql 是最终执行的语句；Description 是给人在预览弹窗里看的说明。
/// </summary>
/// <param name="Kind">动作种类</param>
/// <param name="TableName">目标表（不含 schema）</param>
/// <param name="Sql">要执行的 SQL</param>
/// <param name="IsDestructive">是否会不可逆地丢数据</param>
/// <param name="Description">人类可读说明，破坏性动作必须写清后果</param>
public sealed record MigrationStep(
    MigrationStepKind Kind,
    string TableName,
    string Sql,
    bool IsDestructive,
    string Description);
```

路径：`src/PlcDataHub.Core/Migration/MigrationPlan.cs`

```csharp
namespace PlcDataHub.Core.Migration;

/// <summary>
/// 迁移差异计划。规格 4.1 节：先生成计划返回预览，用户确认后才执行。
/// </summary>
public sealed record MigrationPlan(IReadOnlyList<MigrationStep> Steps)
{
    /// <summary>空计划表示配置与库结构一致，不需要任何 DDL。这是幂等性的判定依据。</summary>
    public bool IsEmpty => Steps.Count == 0;

    /// <summary>会丢数据的动作。预览界面必须把这些高亮出来。</summary>
    public IReadOnlyList<MigrationStep> DestructiveSteps =>
        Steps.Where(s => s.IsDestructive).ToList();
}

/// <summary>迁移选项。默认值是"安全优先"：软删除、不删表。</summary>
/// <param name="SoftDeleteRemovedColumns">删除采集点时改名为 deleted_* 保留数据，而不是 DROP。默认 true</param>
/// <param name="DropRemovedTables">采集组被删除时是否连表一起删。默认 false</param>
public sealed record MigrationOptions(
    bool SoftDeleteRemovedColumns = true,
    bool DropRemovedTables = false);
```

路径：`src/PlcDataHub.Core/Migration/MigrationConflictException.cs`

```csharp
namespace PlcDataHub.Core.Migration;

/// <summary>
/// 类型冲突。规格 4.2 节：类型变更不自动执行，必须报错拦住，由人决定如何处理。
/// </summary>
public sealed class MigrationConflictException : Exception
{
    public MigrationConflictException(string message) : base(message)
    {
    }
}
```

路径：`src/PlcDataHub.Core/Migration/MigrationPlanner.cs`

```csharp
using System.Globalization;
using System.Text;
using PlcDataHub.Core.Model;
using PlcDataHub.Core.Naming;

namespace PlcDataHub.Core.Migration;

/// <summary>
/// 期望结构 vs 现有结构 → 迁移差异计划。规格 4.1 / 4.2 节。
/// 纯计算，不碰数据库。所有 SQL 只生成不执行。
/// </summary>
public static class MigrationPlanner
{
    /// <summary>数据表所在的 schema。规格 3.2 节。</summary>
    public const string DataSchema = "d";

    /// <summary>时间列索引名后缀。</summary>
    private const string TimestampIndexSuffix = "_ts_idx";

    /// <summary>软删除列名的固定前缀。</summary>
    private const string SoftDeletePrefix = "deleted_";

    /// <summary>软删除列名里保留的原列名字符数上限，给前缀与日期留出字节预算。</summary>
    private const int SoftDeleteKeepChars = 40;

    /// <summary>用默认（安全优先）选项生成迁移计划。</summary>
    public static MigrationPlan Plan(
        DesiredSchema desired,
        IReadOnlyList<ExistingTable> existing) =>
        Plan(desired, existing, new MigrationOptions());

    /// <summary>生成迁移计划。</summary>
    /// <exception cref="MigrationConflictException">存在列类型冲突时抛出。</exception>
    public static MigrationPlan Plan(
        DesiredSchema desired,
        IReadOnlyList<ExistingTable> existing,
        MigrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(options);

        var existingByName = existing.ToDictionary(t => t.TableName, StringComparer.Ordinal);
        var desiredNames = desired.Tables.Select(t => t.TableName).ToHashSet(StringComparer.Ordinal);

        var steps = new List<MigrationStep>();

        foreach (var table in desired.Tables)
        {
            if (existingByName.TryGetValue(table.TableName, out var existingTable))
            {
                steps.AddRange(PlanExistingTable(table, existingTable, options));
            }
            else
            {
                steps.AddRange(PlanNewTable(table));
            }
        }

        // 库里有、配置里没有的表
        foreach (var orphan in existing.Where(t => !desiredNames.Contains(t.TableName)))
        {
            if (options.DropRemovedTables)
            {
                steps.Add(new MigrationStep(
                    MigrationStepKind.DropTable,
                    orphan.TableName,
                    $"drop table {Qualified(orphan.TableName)}",
                    IsDestructive: true,
                    $"删除表 {Qualified(orphan.TableName)}，将丢弃 {orphan.RowCount.ToString("N0", CultureInfo.InvariantCulture)} 行数据"));
            }

            // DropRemovedTables=false 时什么都不做：规格 4.2 节，采集组删除默认只停采集、不删表
        }

        return new MigrationPlan(steps);
    }

    private static IEnumerable<MigrationStep> PlanNewTable(DesiredTable table)
    {
        var columnDefs = table.Columns.Select(ColumnDefinitionSql);

        yield return new MigrationStep(
            MigrationStepKind.CreateTable,
            table.TableName,
            $"create table if not exists {Qualified(table.TableName)} (\n  {string.Join(",\n  ", columnDefs)}\n)",
            IsDestructive: false,
            $"新建表 {Qualified(table.TableName)}，共 {table.Columns.Count} 列");

        yield return new MigrationStep(
            MigrationStepKind.CreateIndex,
            table.TableName,
            $"create index if not exists {IndexName(table.TableName)} on {Qualified(table.TableName)} ({DesiredSchema.TimestampColumn})",
            IsDestructive: false,
            $"为 {Qualified(table.TableName)} 建时间索引");
    }

    private static IEnumerable<MigrationStep> PlanExistingTable(
        DesiredTable table,
        ExistingTable existing,
        MigrationOptions options)
    {
        var existingColumns = existing.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);

        foreach (var column in table.Columns)
        {
            if (!existingColumns.TryGetValue(column.Name, out var actual))
            {
                yield return new MigrationStep(
                    MigrationStepKind.AddColumn,
                    table.TableName,
                    $"alter table {Qualified(table.TableName)} add column if not exists {ColumnDefinitionSql(column)}",
                    IsDestructive: false,
                    $"新增列 {column.Name}（历史行该列为 NULL）");
                continue;
            }

            if (!TypesEquivalent(column.PostgresType, actual.PostgresType))
            {
                // 规格 4.2 节：类型变更绝不自动执行
                throw new MigrationConflictException(
                    $"表 {Qualified(table.TableName)} 的列 {column.Name} 类型冲突：" +
                    $"数据库现有类型为 {actual.PostgresType}，配置要求的类型为 {column.PostgresType}。" +
                    "请选择「新建一列」或「确认丢弃该列数据后重建」，软件不会自动修改列类型。");
            }
        }

        // 配置里没有、但库里存在、且不是软删除遗留的列
        var desiredNames = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var actual in existing.Columns.Where(c => !desiredNames.Contains(c.Name)))
        {
            if (actual.Name.StartsWith(SoftDeletePrefix, StringComparison.Ordinal))
            {
                // 已经是软删除遗留列，不再重复处理（这是幂等性的关键一环）
                continue;
            }

            if (options.SoftDeleteRemovedColumns)
            {
                var newName = SoftDeletedName(actual.Name);
                yield return new MigrationStep(
                    MigrationStepKind.SoftDeleteColumn,
                    table.TableName,
                    $"alter table {Qualified(table.TableName)} rename column \"{actual.Name}\" to \"{newName}\"",
                    IsDestructive: false,
                    $"列 {actual.Name} 已不在配置中，改名为 {newName} 保留数据");
            }
            else
            {
                yield return new MigrationStep(
                    MigrationStepKind.DropColumn,
                    table.TableName,
                    $"alter table {Qualified(table.TableName)} drop column \"{actual.Name}\"",
                    IsDestructive: true,
                    $"删除列 {actual.Name}，将丢弃该列全部历史数据（表共 {existing.RowCount.ToString("N0", CultureInfo.InvariantCulture)} 行）");
            }
        }
    }

    private static string ColumnDefinitionSql(DesiredColumn column)
    {
        var nullability = column.IsNullable ? "null" : "not null";
        return $"\"{column.Name}\" {column.PostgresType} {nullability}";
    }

    /// <summary>软删除列名：deleted_{截断的原列名}_{yyyymmdd}，并保证不超 63 字节。</summary>
    private static string SoftDeletedName(string originalName)
    {
        var date = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var suffix = $"_{date}";
        var budget = ColumnNameGenerator.MaxIdentifierBytes
                     - Encoding.UTF8.GetByteCount(SoftDeletePrefix)
                     - Encoding.UTF8.GetByteCount(suffix);

        var keep = originalName.Length > SoftDeleteKeepChars
            ? originalName[..SoftDeleteKeepChars]
            : originalName;

        while (Encoding.UTF8.GetByteCount(keep) > budget && keep.Length > 0)
        {
            keep = keep[..^1];
        }

        return SoftDeletePrefix + keep + suffix;
    }

    private static string IndexName(string tableName) => tableName + TimestampIndexSuffix;

    private static string Qualified(string tableName) => $"{DataSchema}.{tableName}";

    /// <summary>
    /// 类型等价判定。期望结构里写的是标准名（integer / timestamp），
    /// 而 information_schema 返回的是 int4 / timestamp without time zone 这类别名，
    /// 必须视为等价，否则每次迁移都会误判为冲突。
    /// </summary>
    private static bool TypesEquivalent(string desired, string actual)
    {
        static string Canonical(string type) => type.Trim().ToLowerInvariant() switch
        {
            "int" or "int4" or "integer" => "integer",
            "int2" or "smallint" => "smallint",
            "int8" or "bigint" => "bigint",
            "bool" or "boolean" => "boolean",
            "float4" or "real" => "real",
            "float8" or "double precision" => "double precision",
            "timestamp" or "timestamp without time zone" => "timestamp",
            "timestamptz" or "timestamp with time zone" => "timestamptz",
            "varchar" or "character varying" => "text",
            var other => other,
        };

        return Canonical(desired) == Canonical(actual);
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```powershell
dotnet test tests\PlcDataHub.Core.Tests --filter MigrationPlannerTests
```
Expected: `Passed!`。若 `类型等价写法不算冲突` 失败，检查 `TypesEquivalent` 的别名表是否覆盖了 `int2`/`int4`/`timestamp without time zone`。

- [ ] **Step 5: 补一个真实的幂等回归测试**

在 `MigrationPlannerTests.cs` 追加：模拟"执行完计划后"的库状态，再规划一次，必须得到空计划。

```csharp
    [Fact]
    public void 执行计划后再规划一次必须得到空计划()
    {
        // 第一次：库是空的
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"), ("src_ts", "timestamp"), ("wen_du", "double precision"));
        var firstPlan = MigrationPlanner.Plan(desired, Array.Empty<ExistingTable>(), SafeOptions);
        firstPlan.IsEmpty.Should().BeFalse();

        // 第二次：库已经是第一次计划执行完的样子（含 ts 索引，索引不影响列规划）
        var afterExecution = Existing("plc01_fast",
            ("ts", "timestamp"), ("q", "smallint"), ("src_ts", "timestamp"), ("wen_du", "double precision"));
        var secondPlan = MigrationPlanner.Plan(desired, new[] { afterExecution }, SafeOptions);

        secondPlan.IsEmpty.Should().BeTrue("验收项 A12 要求迁移幂等");
    }

    [Fact]
    public void 软删除遗留列在后续迁移中不再被重复改名()
    {
        // 上一轮把 jiu_dian 软删成了 deleted_jiu_dian_20260209
        var desired = Table("plc01_fast", ("ts", "timestamp"), ("q", "smallint"));
        var existing = Existing("plc01_fast",
            ("ts", "timestamp"), ("q", "smallint"), ("deleted_jiu_dian_20260209", "double precision"));

        var plan = MigrationPlanner.Plan(desired, new[] { existing }, SafeOptions);

        plan.IsEmpty.Should().BeTrue("软删除遗留列不应被反复改名，否则迁移不幂等");
    }
```

- [ ] **Step 6: 运行全部测试**

Run:
```powershell
dotnet test
```
Expected: `Passed!`，无失败。

- [ ] **Step 7: 提交**

```powershell
git add <本任务改动的显式路径>
git commit -m "feat: 迁移差异引擎（建表/加列/软删除/类型冲突拦截/幂等）

- 删点默认软删除为 deleted_* 保留数据
- 类型冲突抛 MigrationConflictException 硬拦截，绝不自动改类型
- 类型别名归一（int4→integer、timestamp without time zone→timestamp）
- 幂等：软删除遗留列不再重复处理"
```

---

## Task 6：读块合并与字节解码

对应规格 5.2 节。**这里有个最危险的失效模式**：合并读回多余字节后解码错位，数据是错的但不报错。

**Files:**
- Create: `src/PlcDataHub.Protocols/PlcDataHub.Protocols.csproj`
- Create: `src/PlcDataHub.Protocols/IPlcConnection.cs`
- Create: `src/PlcDataHub.Protocols/ReadBlockPlanner.cs`
- Create: `src/PlcDataHub.Protocols/ByteDecoder.cs`
- Create: `tests/PlcDataHub.Protocols.Tests/PlcDataHub.Protocols.Tests.csproj`
- Create: `tests/PlcDataHub.Protocols.Tests/ReadBlockPlannerTests.cs`
- Create: `tests/PlcDataHub.Protocols.Tests/ByteDecoderTests.cs`

**Interfaces:**
- Consumes: `PointConfig` / `ModbusAddress` / `ModbusRegisterArea` / `PointDataType` / `ByteOrder`（Task 2）
- Produces:
  - `record ReadBlock(int StartAddress, int Length, IReadOnlyList<PointConfig> Points)` —— StartAddress 为寄存器地址（Modbus）或字节偏移（S7）
  - `static class ReadBlockPlanner { static IReadOnlyList<ReadBlock> PlanForModbus(IEnumerable<PointConfig> points, int mergeWindowRegisters, int maxRegistersPerRequest); }`
  - `static class ByteDecoder`
  - `static double? DecodeNumeric(ReadOnlySpan<byte> buffer, int byteOffset, PointDataType type, ByteOrder order)`
  - `static bool DecodeBool(ReadOnlySpan<byte> buffer, int byteOffset, int bitOffset)`

- [ ] **Step 1: 写读块合并的失败测试**

路径：`tests/PlcDataHub.Protocols.Tests/ReadBlockPlannerTests.cs`

```csharp
using FluentAssertions;
using PlcDataHub.Core.Model;
using PlcDataHub.Protocols;
using Xunit;

namespace PlcDataHub.Protocols.Tests;

public class ReadBlockPlannerTests
{
    [Fact]
    public void 连续地址合并成一次请求()
    {
        var points = new[]
        {
            Point(1, 100), Point(2, 101), Point(3, 102), Point(4, 103),
        };

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.Should().ContainSingle();
        blocks[0].StartAddress.Should().Be(100);
        blocks[0].Length.Should().Be(4);
        blocks[0].Points.Should().HaveCount(4);
    }

    [Fact]
    public void 地址间隔在窗口内时合并并留下空洞()
    {
        var points = new[] { Point(1, 100), Point(2, 105) };

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.Should().ContainSingle();
        blocks[0].StartAddress.Should().Be(100);
        blocks[0].Length.Should().Be(6, "要把空洞一起读回来，换取更少的请求次数");
    }

    [Fact]
    public void 地址间隔超出窗口时拆成两个请求()
    {
        var points = new[] { Point(1, 100), Point(2, 200) };

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.Should().HaveCount(2);
        blocks[0].StartAddress.Should().Be(100);
        blocks[1].StartAddress.Should().Be(200);
    }

    [Fact]
    public void 单个请求长度不超过上限()
    {
        // 300 个连续寄存器，上限 120，必须切成多块
        var points = Enumerable.Range(0, 300).Select(i => Point(i + 1, 1000 + i)).ToArray();

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.Should().NotBeEmpty();
        blocks.Should().OnlyContain(b => b.Length <= 120);
        blocks.Sum(b => b.Points.Count).Should().Be(300, "不能漏点也不能重复");
    }

    [Fact]
    public void 不同采集点类型占用不同寄存器数()
    {
        // REAL 占 2 个寄存器，所以 100 与 102 是连续的
        var real = Point(1, 100, PointDataType.Real);
        var next = Point(2, 102, PointDataType.Word);

        var blocks = ReadBlockPlanner.PlanForModbus(new[] { real, next }, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.Should().ContainSingle();
        blocks[0].Length.Should().Be(3, "REAL 占 2 个寄存器 + WORD 占 1 个");
    }

    [Fact]
    public void 空输入返回空计划()
    {
        ReadBlockPlanner.PlanForModbus(Array.Empty<PointConfig>(), 16, 120).Should().BeEmpty();
    }

    [Fact]
    public void 重复地址不会导致点丢失()
    {
        var points = new[] { Point(1, 100), Point(2, 100) };

        var blocks = ReadBlockPlanner.PlanForModbus(points, mergeWindowRegisters: 16, maxRegistersPerRequest: 120);

        blocks.SelectMany(b => b.Points).Should().HaveCount(2);
    }

    private static PointConfig Point(int id, int register, PointDataType type = PointDataType.Word) => new(
        PointId: id, PointCode: $"p{id}", PointName: $"点{id}", ColumnName: $"p{id}",
        DataType: type, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
        S7: null, Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, register));
}
```

- [ ] **Step 2: 写字节解码的失败测试**

路径：`tests/PlcDataHub.Protocols.Tests/ByteDecoderTests.cs`

```csharp
using FluentAssertions;
using PlcDataHub.Core.Model;
using PlcDataHub.Protocols;
using Xunit;

namespace PlcDataHub.Protocols.Tests;

public class ByteDecoderTests
{
    [Fact]
    public void 大端_REAL_解码正确()
    {
        // 25.5f 的大端表示：0x41CC0000
        byte[] buffer = [0x41, 0xCC, 0x00, 0x00];

        ByteDecoder.DecodeNumeric(buffer, 0, PointDataType.Real, ByteOrder.Big)
            .Should().BeApproximately(25.5, 0.0001);
    }

    [Fact]
    public void 小端_REAL_解码正确()
    {
        byte[] buffer = [0x00, 0x00, 0xCC, 0x41];

        ByteDecoder.DecodeNumeric(buffer, 0, PointDataType.Real, ByteOrder.Little)
            .Should().BeApproximately(25.5, 0.0001);
    }

    [Fact]
    public void 有偏移时解码正确()
    {
        byte[] buffer = [0xFF, 0xFF, 0x41, 0xCC, 0x00, 0x00, 0xFF];

        ByteDecoder.DecodeNumeric(buffer, 2, PointDataType.Real, ByteOrder.Big)
            .Should().BeApproximately(25.5, 0.0001);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x64 }, PointDataType.Word, 100.0)]      // 大端 100
    [InlineData(new byte[] { 0x64, 0x00 }, PointDataType.Word, 25600.0)]    // 大端 25600
    [InlineData(new byte[] { 0xFF, 0x9C }, PointDataType.Int, -100.0)]       // 大端有符号 -100
    public void 整数类型解码正确(byte[] buffer, PointDataType type, double expected)
    {
        ByteDecoder.DecodeNumeric(buffer, 0, type, ByteOrder.Big).Should().Be(expected);
    }

    [Fact]
    public void 缓冲区不足时返回_null_而不是读越界()
    {
        byte[] buffer = [0x41, 0xCC];

        ByteDecoder.DecodeNumeric(buffer, 0, PointDataType.Real, ByteOrder.Big).Should().BeNull();
    }

    [Fact]
    public void 偏移超出缓冲区时返回_null()
    {
        byte[] buffer = [0x41, 0xCC, 0x00, 0x00];

        ByteDecoder.DecodeNumeric(buffer, 10, PointDataType.Real, ByteOrder.Big).Should().BeNull();
    }

    [Theory]
    [InlineData(0b0000_0001, 0, true)]
    [InlineData(0b0000_0001, 1, false)]
    [InlineData(0b1000_0000, 7, true)]
    [InlineData(0b0000_0000, 3, false)]
    public void 位解码正确(byte value, int bitOffset, bool expected)
    {
        ByteDecoder.DecodeBool(new[] { value }, 0, bitOffset).Should().Be(expected);
    }

    [Fact]
    public void 位解码缓冲区不足时返回_null()
    {
        ByteDecoder.DecodeBool(ReadOnlySpan<byte>.Empty, 0, 0).Should().BeNull();
    }

    [Fact]
    public void 合并块中的空洞位置不会导致相邻点解码错位()
    {
        // 这是规格 9 节里标记为"最危险"的失效模式：
        // 100 处是 WORD，103 处是 REAL，中间 101~102 是空洞（一起读回来了）
        byte[] buffer =
        [
            0x00, 0x01,             // 偏移 0-1: WORD = 1          (寄存器 100)
            0xDE, 0xAD,             // 偏移 2-3: 空洞               (寄存器 101)
            0x41, 0xCC, 0x00, 0x00, // 偏移 4-7: REAL = 25.5       (寄存器 102-103)
        ];

        ByteDecoder.DecodeNumeric(buffer, 0, PointDataType.Word, ByteOrder.Big).Should().Be(1.0);
        ByteDecoder.DecodeNumeric(buffer, 4, PointDataType.Real, ByteOrder.Big)
            .Should().BeApproximately(25.5, 0.0001);
    }
}
```

- [ ] **Step 3: 建立协议项目与测试项目**

Run:
```powershell
cd D:\deepseek\PLC_Collect
dotnet new classlib -o src\PlcDataHub.Protocols
dotnet new xunit -o tests\PlcDataHub.Protocols.Tests --no-restore
dotnet sln add src\PlcDataHub.Protocols\PlcDataHub.Protocols.csproj tests\PlcDataHub.Protocols.Tests\PlcDataHub.Protocols.Tests.csproj
dotnet add tests\PlcDataHub.Protocols.Tests reference src\PlcDataHub.Protocols
dotnet add tests\PlcDataHub.Protocols.Tests reference src\PlcDataHub.Core
dotnet add src\PlcDataHub.Protocols reference src\PlcDataHub.Core
```

> **⚠️ 实测陷阱一：不要加 `-f net8.0`。**
> 本机 .NET 10 SDK 的模板**只支持 `-f net10.0`**（已实测，传 `-f net8.0` 会失败退出码 127）。
> 正确做法是**按模板默认生成，然后手工把 csproj 里的 `<TargetFramework>` 行整个删掉**，
> 由 `Directory.Build.props` 统一提供 `net8.0-windows`。
> 两个 csproj 都要处理：`PlcDataHub.Protocols.csproj` 与 `PlcDataHub.Protocols.Tests.csproj`。
> 测试 csproj 还需删掉模板写入的 `<IsPackable>` 之外的样板并保留 `IsPackable=false`。

> **⚠️ 实测陷阱二：项目若嵌套在子目录，必须在父 csproj 里排除。**
> SDK 风格项目默认编译目录树下所有 `.cs`。本机实测把测试项目建在类库子目录时，
> 类库会去编译测试源码并报 `未能找到类型或命名空间名 "Xunit"`，
> 且错误指向**类库项目**，极易误判。
> 本计划的结构里 `src\` 与 `tests\` 是平级的，不会触发；
> 但若执行者改用嵌套布局，必须在父 csproj 加：
> ```xml
> <Compile Remove="tests\**" />
> <None Remove="tests\**" />
> ```

然后把两个测试文件的内容按 Step 1 / Step 2 写入，删除模板生成的 `UnitTest1.cs` / `Class1.cs`。

再运行：
```powershell
dotnet test tests\PlcDataHub.Protocols.Tests
```
Expected: 编译失败，`ReadBlockPlanner` / `ByteDecoder` 未定义。

- [ ] **Step 4: 写实现**

路径：`src/PlcDataHub.Protocols/IPlcConnection.cs`

```csharp
using PlcDataHub.Core.Model;

namespace PlcDataHub.Protocols;

/// <summary>
/// 一次读请求覆盖的地址范围。
/// StartAddress 对 Modbus 是寄存器地址，对 S7 是字节偏移。
/// Points 里所有点的地址都落在 [StartAddress, StartAddress + Length) 内。
/// </summary>
public sealed record ReadBlock(int StartAddress, int Length, IReadOnlyList<PointConfig> Points);

/// <summary>一次采集回合的结果：点 → 值。值为 null 表示该点本次读取失败（坏点）。</summary>
public sealed record ReadResult(IReadOnlyDictionary<PointConfig, object?> Values);

/// <summary>
/// 协议抽象。规格 5.1 节：一条连接由一个线程独占，绝不并发访问同一实例。
/// </summary>
public interface IPlcConnection : IDisposable
{
    /// <summary>建立连接。失败抛异常，由采集器的重连逻辑处理。</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>读取一组点。返回的字典必须为每个传入的点都给出条目，失败的点值为 null。</summary>
    Task<ReadResult> ReadAsync(IReadOnlyList<PointConfig> points, CancellationToken cancellationToken);

    /// <summary>当前是否处于已连接状态。</summary>
    bool IsConnected { get; }
}
```

路径：`src/PlcDataHub.Protocols/ReadBlockPlanner.cs`

```csharp
using PlcDataHub.Core.Model;

namespace PlcDataHub.Protocols;

/// <summary>
/// 读块合并。规格 5.2 节。
/// 不合并时 200 个点 = 200 次请求，1 秒周期下任何 PLC 都扛不住；
/// 合并后通常只需 5~15 次请求。
/// </summary>
public static class ReadBlockPlanner
{
    /// <summary>
    /// 为 Modbus 点集规划读块。
    /// </summary>
    /// <param name="points">采集点，内部会按地址排序</param>
    /// <param name="mergeWindowRegisters">合并窗口：相邻地址间隔不超过该值就合并（留空洞换取更少请求）</param>
    /// <param name="maxRegistersPerRequest">单次请求的寄存器数上限</param>
    public static IReadOnlyList<ReadBlock> PlanForModbus(
        IEnumerable<PointConfig> points,
        int mergeWindowRegisters,
        int maxRegistersPerRequest)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (mergeWindowRegisters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(mergeWindowRegisters), "合并窗口必须至少为 1");
        }

        if (maxRegistersPerRequest < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRegistersPerRequest), "单次请求上限必须至少为 1");
        }

        var ordered = points
            .Where(p => p.Modbus is not null)
            .OrderBy(p => p.Modbus!.RegisterAddress)
            .ThenBy(p => p.PointId)
            .ToList();

        if (ordered.Count == 0)
        {
            return Array.Empty<ReadBlock>();
        }

        var blocks = new List<ReadBlock>();
        var currentPoints = new List<PointConfig> { ordered[0] };
        var blockStart = ordered[0].Modbus!.RegisterAddress;
        var blockEnd = blockStart + RegisterWidth(ordered[0]);

        for (var i = 1; i < ordered.Count; i++)
        {
            var point = ordered[i];
            var pointStart = point.Modbus!.RegisterAddress;
            var pointEnd = pointStart + RegisterWidth(point);
            var extendedLength = Math.Max(blockEnd, pointEnd) - blockStart;

            var canMerge = pointStart - blockEnd < mergeWindowRegisters
                           && extendedLength <= maxRegistersPerRequest;

            if (canMerge)
            {
                currentPoints.Add(point);
                blockEnd = Math.Max(blockEnd, pointEnd);
            }
            else
            {
                blocks.Add(new ReadBlock(blockStart, blockEnd - blockStart, currentPoints));

                currentPoints = [point];
                blockStart = pointStart;
                blockEnd = pointEnd;
            }
        }

        blocks.Add(new ReadBlock(blockStart, blockEnd - blockStart, currentPoints));

        return blocks;
    }

    /// <summary>某个点占用多少个 Modbus 寄存器（16 位）。</summary>
    internal static int RegisterWidth(PointConfig point) => point.DataType switch
    {
        PointDataType.Bool => 1,
        PointDataType.Byte => 1,
        PointDataType.Word => 1,
        PointDataType.SInt => 1,
        PointDataType.USInt => 1,
        PointDataType.Int => 1,
        PointDataType.UInt => 1,
        PointDataType.DWord => 2,
        PointDataType.DInt => 2,
        PointDataType.UDInt => 2,
        PointDataType.Real => 2,
        PointDataType.LReal => 4,
        PointDataType.Dtl => 4,
        PointDataType.String => 1,   // Modbus 字符串按字节流处理，具体宽度由地址与长度决定
        _ => throw new ArgumentOutOfRangeException(nameof(point), point.DataType, "未知数据类型"),
    };
}
```

路径：`src/PlcDataHub.Protocols/ByteDecoder.cs`

```csharp
using System.Buffers.Binary;
using PlcDataHub.Core.Model;

namespace PlcDataHub.Protocols;

/// <summary>
/// 字节数组 → 数值。规格 9 节标记的最危险失效模式就在这一层：
/// 合并读回多余字节后若解码错位，数据是错的但不会报错。
/// 因此所有解码都必须显式传 byteOffset，且严格做长度检查。
/// </summary>
public static class ByteDecoder
{
    /// <summary>
    /// 解码一个数值。缓冲区不足时返回 null（视为坏点），绝不抛越界异常。
    /// </summary>
    /// <param name="buffer">本次读请求返回的原始字节（对应 ReadBlock 覆盖的整段范围）</param>
    /// <param name="byteOffset">该点在 buffer 中的起始字节偏移</param>
    /// <param name="type">数据类型</param>
    /// <param name="order">字节序</param>
    public static double? DecodeNumeric(
        ReadOnlySpan<byte> buffer,
        int byteOffset,
        PointDataType type,
        ByteOrder order)
    {
        var width = ByteWidth(type);

        if (byteOffset < 0 || byteOffset + width > buffer.Length)
        {
            return null;
        }

        var slice = buffer.Slice(byteOffset, width);

        return type switch
        {
            PointDataType.Real => DecodeSingle(slice, order),
            PointDataType.LReal => DecodeDouble(slice, order),
            PointDataType.Byte => slice[0],
            PointDataType.USInt => slice[0],
            PointDataType.SInt => (sbyte)slice[0],
            PointDataType.Word => DecodeUInt16(slice, order),
            PointDataType.UInt => DecodeUInt16(slice, order),
            PointDataType.Int => DecodeInt16(slice, order),
            PointDataType.DWord => DecodeUInt32(slice, order),
            PointDataType.UDInt => DecodeUInt32(slice, order),
            PointDataType.DInt => DecodeInt32(slice, order),
            _ => null,
        };
    }

    /// <summary>
    /// 解码一个位。缓冲区不足时返回 null。
    /// </summary>
    /// <param name="buffer">原始字节</param>
    /// <param name="byteOffset">字节偏移</param>
    /// <param name="bitOffset">位偏移 0~7</param>
    public static bool? DecodeBool(ReadOnlySpan<byte> buffer, int byteOffset, int bitOffset)
    {
        if (byteOffset < 0 || byteOffset >= buffer.Length || bitOffset is < 0 or > 7)
        {
            return null;
        }

        return (buffer[byteOffset] & (1 << bitOffset)) != 0;
    }

    /// <summary>某个数据类型占用的字节数。字符串与 DTL 不在此函数处理范围内。</summary>
    internal static int ByteWidth(PointDataType type) => type switch
    {
        PointDataType.Bool => 1,
        PointDataType.Byte => 1,
        PointDataType.SInt => 1,
        PointDataType.USInt => 1,
        PointDataType.Word => 2,
        PointDataType.Int => 2,
        PointDataType.UInt => 2,
        PointDataType.DWord => 4,
        PointDataType.DInt => 4,
        PointDataType.UDInt => 4,
        PointDataType.Real => 4,
        PointDataType.LReal => 8,
        PointDataType.Dtl => 8,
        PointDataType.String => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知数据类型"),
    };

    private static float DecodeSingle(ReadOnlySpan<byte> slice, ByteOrder order)
    {
        var bits = order == ByteOrder.Big
            ? BinaryPrimitives.ReadInt32BigEndian(slice)
            : BinaryPrimitives.ReadInt32LittleEndian(slice);

        return BitConverter.Int32BitsToSingle(bits);
    }

    private static double DecodeDouble(ReadOnlySpan<byte> slice, ByteOrder order)
    {
        var bits = order == ByteOrder.Big
            ? BinaryPrimitives.ReadInt64BigEndian(slice)
            : BinaryPrimitives.ReadInt64LittleEndian(slice);

        return BitConverter.Int64BitsToDouble(bits);
    }

    private static ushort DecodeUInt16(ReadOnlySpan<byte> slice, ByteOrder order) =>
        order == ByteOrder.Big
            ? BinaryPrimitives.ReadUInt16BigEndian(slice)
            : BinaryPrimitives.ReadUInt16LittleEndian(slice);

    private static short DecodeInt16(ReadOnlySpan<byte> slice, ByteOrder order) =>
        order == ByteOrder.Big
            ? BinaryPrimitives.ReadInt16BigEndian(slice)
            : BinaryPrimitives.ReadInt16LittleEndian(slice);

    private static uint DecodeUInt32(ReadOnlySpan<byte> slice, ByteOrder order) =>
        order == ByteOrder.Big
            ? BinaryPrimitives.ReadUInt32BigEndian(slice)
            : BinaryPrimitives.ReadUInt32LittleEndian(slice);

    private static int DecodeInt32(ReadOnlySpan<byte> slice, ByteOrder order) =>
        order == ByteOrder.Big
            ? BinaryPrimitives.ReadInt32BigEndian(slice)
            : BinaryPrimitives.ReadInt32LittleEndian(slice);
}
```

- [ ] **Step 5: 运行测试确认通过**

Run:
```powershell
dotnet test tests\PlcDataHub.Protocols.Tests
```
Expected: `Passed!`。重点确认 `合并块中的空洞位置不会导致相邻点解码错位` 通过——这条是防规格 9 节"最危险失效模式"的回归测试。

- [ ] **Step 6: 提交**

```powershell
git add <本任务改动的显式路径>
git commit -m "feat: 读块合并与字节解码

- ReadBlockPlanner 按窗口合并连续地址，超出单请求上限自动切块
- ByteDecoder 支持大端/小端、有符号/无符号、REAL/LREAL/BOOL
- 严格长度检查，缓冲区不足返回 null 而非越界
- 回归测试覆盖'空洞导致相邻点解码错位'这一最危险失效模式"
```

---

## Task 7：Modbus TCP 协议实现与假连接

### Task 7 必须满足的跨任务约束（由 Task 6 的实际产出推导，非可选）

1. **`ReadAsync` 必须按 `(SlaveId, ModbusRegisterArea)` 分组，每组分别调用 `PlanForModbus`。**
   理由（Task 6 实现者提出 + 控制者补充）：`PlanForModbus` **不校验寄存器区、不按 `SlaveId` 分组**，会把线圈/离散输入/保持寄存器/输入寄存器的点、以及不同从站的点**合并进同一个块** → 发出跨功能码、跨从站的请求。
   这**不只是"可能被设备拒绝"，而是真实的错误**：**寄存器区之间的地址编号不通用**（保持寄存器 100 与输入寄存器 100 是完全不同的寄存器），跨从站同理。
   **规划器签名保持不变**——分组是连接层编排，不是规划器职责。

2. **Modbus 侧字节偏移必须乘 2：`byteOffset = (点寄存器地址 - block.StartAddress) * 2`。**
   `ReadBlock.StartAddress` 对 Modbus 是**寄存器地址**，对 S7 是**字节偏移**——弄混不会抛异常，只会**静默读错字节**。（Task 6 已把该双重语义写进 XML 注释。）

3. **`ByteDecoder` 对 `Bool` / `Dtl` / `String` 抛 `NotSupportedException`，不要试图在连接层绕过。**
   `Bool` 必须走 `DecodeBool(buffer, byteOffset, bitOffset)`；`Dtl` 与 `String` 本期不支持。
   > **⚠️ 接口签名以实现为准：`DecodeBool` 返回 `bool?`，不是 brief 早先写的 `bool`。**
   > （`bool` 装不下"缓冲区不足返回 null"这条契约；Task 6 已按 `bool?` 实现并测试。）
4. **配置不支持的参数类型（`Dtl` / `String`）时，连接层必须在任何 I/O 之前、以点名的方式拒绝，不得等到解码期才抛。**
   理由与演进过程：
   - `Dtl` / `String` 在**规划层仍占带宽**（4 / 1 寄存器），若只在解码期抛，异常会**在 I/O 之后**发生（白发一次请求）、**不含点标识**（`ByteDecoder` 的入参里根本没有 `PointConfig`，结构上不可能点名），且**中断整轮**（一个点的问题导致整组本轮无数据）。
   - > **⚠️ 本条的早期措辞是错的**：原文写"该点本轮写坏值（NULL），其余点正常"。Task 7 审查证明这在连接层**做不到**——`ReadAsync` 一次调用覆盖整组，除非引入"按点捕获 + 错误通道"的新接口，否则无法既冒泡又保留其余点的数据。
   - **正确做法（Task 7 已实施）**：把类型检查放进配置期校验（`EnsurePointAddressable`），在**任何 I/O 之前**抛出**带点标识**的 `NotSupportedException`。这是 **fail-fast**：配置错误是操作员必须修的东西，让它在最早、可归因的位置暴露，比"每周期静默坏值"好得多。解码器侧的类型检查**保留为第二道防线**（正常路径不可达）。
5. **连接必须暴露 `LastError`，使"点持续坏值"可归因。**
   理由：从站返回异常码（01 不支持该功能码 / 02 地址超设备范围）**恰是现场最常见的配置错误形态**，而它按规格 5.3 被降级为坏点（正确：链路是好的，不该重连）。但若**没有任何错误信号**，它就和"偶发读失败"完全不可区分——"某个点每周期静默写 NULL、无人知道为什么"，与本项目最怕的失效同类。
   → 连接层在 `SlaveException` 与传输失败分支都写入 `LastError`，供 Plan 2 报进 `rt.group_status`。

6. **采集器必须区分"链路已失效"与"本块坏点"，并接受同一个现场故障可能落在不同类别。**
   `ConnectionFailureKind` 有三类：`SlaveRejected`（链路好、该块被拒）/ `Transport`（链路坏）/ `Unexpected`（冒泡的未预期异常）。
   **关键约束（实测依据）**：同一个现场故障（设备消失 / 对端 RST）**可能先表现为 `Transport`、也可能先表现为 `Unexpected`**，取决于首次失败的时序。
   → **Plan 2 的状态层应把 `Transport` 与 `Unexpected` 都按"链路不可用、该重连"展示，只在日志里区分**（前者查现场链路、后者查软件缺陷）。**不要按类别做不同的告警分级**——那会把同一故障显示成两种严重程度。
   > 另注：`LastError` 是**粘性**语义（成功不清空），Plan 2 不能把它直接当"当前状态"展示，必须配合 `IsConnected` 与本轮结果。
   > 另注：`IsConnected == false` 后**只有 `ConnectAsync` 能置回 true**——采集器的重连逻辑必须在该状态下调用 `ConnectAsync`，否则会一直拿到"尚未连接"。

### 已知模型限制（不得在本任务"顺手绕过"，需保持现状）

- **小端语义 = 整值字节逆序，不是 word-swapped。** 真实 Modbus 设备有四种字节序（ABCD / DCBA / BADC / CDAB），而 `ByteOrder` 只有 `Big`/`Little`，**无法表达 word-swapped（BADC / CDAB）**。若现场遇到此类设备，需扩模型而非在连接层做特例。
- **`String` 无长度来源**：`PointConfig` 没有字符串长度字段，`ByteWidth`/`RegisterWidth` 对 `String` 都给占位值 1。**"Modbus 字符串"在 Plan 1 数据模型里无法表达。**

> **模型前置条件（Task 7 已落地，后续不得回退）**：`ModbusAddress` 现有 **4** 个字段：
> ```csharp
> public sealed record ModbusAddress(int SlaveId, ModbusRegisterArea Area, int RegisterAddress, int BitOffset = 0);
> ```
> `BitOffset` 语义务必分清：**位区**（`Coil`/`DiscreteInput`）每个地址本身就是一位，`BitOffset` 必须为 **0**；**寄存器区**（`HoldingRegister`/`InputRegister`）的 `Bool` 点用 `BitOffset ∈ 0~15` 指定寄存器内的位。
> 该字段是 Task 7 为修一个真实缺陷而加的（`ModbusAddress` 无位偏移时，"保持寄存器的第 3 位"无处表达）；它同时触发了 Task 2 的护栏，`ConfigComparer` 已同步比较该字段。**删掉它会让寄存器内位寻址重新变得无法表达。**

### 本机环境限制（影响验收构造，Plan 2 必须知悉）

- **对不可达外网 IP 的连接会立即返回"已连接"**（疑似代理拦截）→ `ConnectAsync` 的**超时分支在本机构造不出来**。要构造"连不上"，只能用**环回端口拒绝**（真拒绝，约 2 秒）或真实设备。
  → **Plan 2 的 A3（PLC 断线重连）与"连不上要报警"类验收不得依赖外网地址。**
- 宿主是 **Windows PowerShell**（`pwsh` 不在 PATH）、执行策略**禁止运行 `.ps1`** → 脚本需用 `[scriptblock]::Create` 执行，或写成内联命令。

**Files:**
- Create: `src/PlcDataHub.Protocols/Modbus/ModbusTcpConnection.cs`
- Create: `src/PlcDataHub.Protocols/Fake/FakePlcConnection.cs`
- Modify: `src/PlcDataHub.Protocols/PlcDataHub.Protocols.csproj`（加 NModbus 引用）
- Create: `tests/PlcDataHub.Protocols.Tests/FakePlcConnectionTests.cs`

**Interfaces:**
- Consumes: `IPlcConnection` / `ReadBlock` / `ReadResult`（Task 6）、`ReadBlockPlanner` / `ByteDecoder`（Task 6）、`DeviceConnection` / `PointConfig`（Task 2）
- Produces:
  - `sealed class ModbusTcpConnection : IPlcConnection`，构造 `ModbusTcpConnection(DeviceConnection connection, ModbusTcpOptions? options = null)`
  - `record ModbusTcpOptions(int MergeWindowRegisters = 16, int MaxRegistersPerRequest = 120, int TimeoutMs = 1000)`
  - `sealed class FakePlcConnection : IPlcConnection`，构造 `FakePlcConnection(FakePlcScript script)`
  - `sealed record FakePlcScript(IReadOnlyList<FakePlcResponse> Responses)` —— 按调用次序返回脚本化的响应，用于精确构造现场故障序列
  - `sealed record FakePlcResponse(bool FailConnect, bool FailRead, string? ErrorMessage, IReadOnlyDictionary<int, byte[]> DataByPointId)`

- [ ] **Step 1: 先验证 NModbus 在 .NET 8 上可用（依赖风险验证）**

规格 8.1 节把这一步列为必做。Run:
```powershell
cd D:\deepseek\PLC_Collect
dotnet add src\PlcDataHub.Protocols package NModbus
dotnet build src\PlcDataHub.Protocols
```
Expected: 还原并编译成功。

**若失败**：改为自己实现 Modbus TCP 帧层（协议简单：MBAP 头 7 字节 + PDU，功能码 03 读保持寄存器 + 01 读线圈，CRC 仅 RTU 需要，TCP 不需要）。此时把 `ModbusTcpConnection` 的实现替换为手写 socket 版本，测试保持不变。**不要因为依赖不可用而停下。**

- [ ] **Step 2: 写假连接的失败测试**

路径：`tests/PlcDataHub.Protocols.Tests/FakePlcConnectionTests.cs`

```csharp
using FluentAssertions;
using PlcDataHub.Core.Model;
using PlcDataHub.Protocols;
using PlcDataHub.Protocols.Fake;
using Xunit;

namespace PlcDataHub.Protocols.Tests;

public class FakePlcConnectionTests
{
    [Fact]
    public async Task 脚本指定连接失败时_Connect_抛异常()
    {
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(FailConnect: true, FailRead: false, ErrorMessage: "连接被拒绝", DataByPointId: new Dictionary<int, byte[]>()),
        });
        using var connection = new FakePlcConnection(script);

        var act = async () => await connection.ConnectAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*连接被拒绝*");
    }

    [Fact]
    public async Task 脚本指定读失败时返回_null_而不抛异常()
    {
        // 规格 5.3 节：单轮采集失败只影响本轮，坏点写 NULL，不能让异常冒出去
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(FailConnect: false, FailRead: false, null, new Dictionary<int, byte[]>()),
            new FakePlcResponse(FailConnect: false, FailRead: true, "读超时", new Dictionary<int, byte[]>()),
        });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        var points = new[] { Point(1) };
        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().BeNull();
    }

    [Fact]
    public async Task 脚本可以提供真实字节用于端到端解码验证()
    {
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]> { [1] = [0x00, 0x64] }),
        });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        var points = new[] { Point(1) };
        var result = await connection.ReadAsync(points, CancellationToken.None);

        result.Values[points[0]].Should().Be(100.0);
    }

    [Fact]
    public async Task 响应脚本耗尽后重复返回最后一个响应()
    {
        // 便于写"一直正常"或"一直失败"的长期测试
        var script = new FakePlcScript(new[]
        {
            new FakePlcResponse(false, false, null, new Dictionary<int, byte[]> { [1] = [0x00, 0x0A] }),
        });
        using var connection = new FakePlcConnection(script);
        await connection.ConnectAsync(CancellationToken.None);

        var points = new[] { Point(1) };
        for (var i = 0; i < 5; i++)
        {
            var result = await connection.ReadAsync(points, CancellationToken.None);
            result.Values[points[0]].Should().Be(10.0);
        }
    }

    private static PointConfig Point(int id) => new(
        PointId: id, PointCode: $"p{id}", PointName: $"点{id}", ColumnName: $"p{id}",
        DataType: PointDataType.Word, ByteOrder: ByteOrder.Big, Scale: 1.0, Offset: 0.0, Enabled: true,
        S7: null, Modbus: new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100));
}
```

- [ ] **Step 3: 运行测试确认失败**

Run:
```powershell
dotnet test tests\PlcDataHub.Protocols.Tests --filter FakePlcConnectionTests
```
Expected: 编译失败，`FakePlcConnection` 未定义。

- [ ] **Step 4: 写实现**

路径：`src/PlcDataHub.Protocols/Fake/FakePlcConnection.cs`

```csharp
using PlcDataHub.Core.Model;

namespace PlcDataHub.Protocols.Fake;

/// <summary>
/// 脚本化响应：按调用次序依次返回，用于精确构造现场故障序列
/// （连上 → 采两轮 → 读超时 → 断开 → 重连成功 → 恢复采集）。
/// </summary>
/// <param name="FailConnect">true 表示本次 Connect 失败</param>
/// <param name="FailRead">true 表示本次 Read 整体失败（所有点为坏点）</param>
/// <param name="ErrorMessage">失败时的错误信息</param>
/// <param name="DataByPointId">成功时各点的原始字节，键为 PointId</param>
public sealed record FakePlcResponse(
    bool FailConnect,
    bool FailRead,
    string? ErrorMessage,
    IReadOnlyDictionary<int, byte[]> DataByPointId);

/// <summary>假连接的响应脚本。耗尽后重复返回最后一个响应。</summary>
public sealed record FakePlcScript(IReadOnlyList<FakePlcResponse> Responses);

/// <summary>
/// 假 PLC 连接。用途有两个：
/// ① 单元/状态机测试——不需要真实设备就能构造任意故障序列；
/// ② 现场无 PLC 时验证"建表 + 采集 + 入库"链路。
/// </summary>
public sealed class FakePlcConnection : IPlcConnection
{
    private readonly FakePlcScript _script;
    private int _callIndex;

    public FakePlcConnection(FakePlcScript script)
    {
        _script = script ?? throw new ArgumentNullException(nameof(script));

        if (script.Responses.Count == 0)
        {
            throw new ArgumentException("响应脚本不能为空", nameof(script));
        }
    }

    public bool IsConnected { get; private set; }

    /// <summary>Connect 被调用的次数。用于验证重连逻辑真的在重连。</summary>
    public int ConnectAttempts { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectAttempts++;
        var response = Next();

        if (response.FailConnect)
        {
            IsConnected = false;
            throw new InvalidOperationException(response.ErrorMessage ?? "假连接：连接失败");
        }

        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task<ReadResult> ReadAsync(IReadOnlyList<PointConfig> points, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);

        var response = Next();
        var values = new Dictionary<PointConfig, object?>(points.Count);

        if (response.FailRead)
        {
            IsConnected = false;

            // 规格 5.3 节：坏点写 NULL，绝不抛异常冒泡
            foreach (var point in points)
            {
                values[point] = null;
            }

            return Task.FromResult(new ReadResult(values));
        }

        foreach (var point in points)
        {
            if (!response.DataByPointId.TryGetValue(point.PointId, out var bytes))
            {
                values[point] = null;
                continue;
            }

            values[point] = point.DataType == PointDataType.Bool
                ? ByteDecoder.DecodeBool(bytes, 0, point.S7?.BitOffset ?? 0)
                : ByteDecoder.DecodeNumeric(bytes, 0, point.DataType, point.ByteOrder);
        }

        return Task.FromResult(new ReadResult(values));
    }

    public void Dispose() => IsConnected = false;

    /// <summary>取下一个响应；脚本耗尽后重复返回最后一个，便于写长期测试。</summary>
    private FakePlcResponse Next()
    {
        var index = Math.Min(_callIndex, _script.Responses.Count - 1);
        _callIndex++;
        return _script.Responses[index];
    }
}
```

路径：`src/PlcDataHub.Protocols/Modbus/ModbusTcpConnection.cs`

```csharp
using PlcDataHub.Core.Model;

namespace PlcDataHub.Protocols.Modbus;

/// <summary>Modbus TCP 采集选项。默认值与规格 5.2 节一致。</summary>
/// <param name="MergeWindowRegisters">合并窗口（寄存器）</param>
/// <param name="MaxRegistersPerRequest">单请求寄存器上限</param>
/// <param name="TimeoutMs">单次请求超时（毫秒）</param>
public sealed record ModbusTcpOptions(
    int MergeWindowRegisters = 16,
    int MaxRegistersPerRequest = 120,
    int TimeoutMs = 1000);

/// <summary>
/// Modbus TCP 连接。规格 5.1 节：本类实例由单个采集线程独占，
/// 内部的 socket 绝不并发访问——多于一个线程同时用它必然串帧。
/// </summary>
public sealed class ModbusTcpConnection : IPlcConnection
{
    private readonly DeviceConnection _connection;
    private readonly ModbusTcpOptions _options;
    private IModbusMaster? _master;

    public ModbusTcpConnection(DeviceConnection connection, ModbusTcpOptions? options = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _options = options ?? new ModbusTcpOptions();

        if (connection.Protocol != ProtocolKind.ModbusTcp)
        {
            throw new ArgumentException($"协议必须是 ModbusTcp，实际为 {connection.Protocol}", nameof(connection));
        }

        if (string.IsNullOrWhiteSpace(connection.Host))
        {
            throw new ArgumentException("Modbus TCP 必须配置 Host", nameof(connection));
        }
    }

    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        DisposeMaster();

        var factory = new ModbusFactory();
        var client = new System.Net.Sockets.TcpClient();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.TimeoutMs);

        try
        {
            await client.ConnectAsync(_connection.Host!, _connection.Port, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"连接 {_connection.Host}:{_connection.Port} 超时（{_options.TimeoutMs} ms）");
        }

        client.NoDelay = true;
        _master = factory.CreateMaster(client);
        _master.Transport.ReadTimeout = _options.TimeoutMs;
        _master.Transport.WriteTimeout = _options.TimeoutMs;
        IsConnected = true;
    }

    public Task<ReadResult> ReadAsync(IReadOnlyList<PointConfig> points, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (_master is null || !IsConnected)
        {
            throw new InvalidOperationException("尚未连接，不能读取");
        }

        var values = new Dictionary<PointConfig, object?>(points.Count);

        // 按寄存器区分别规划读块：功能码 03 与 04 不能混在一次请求里
        foreach (var areaGroup in points
                     .Where(p => p.Modbus is not null)
                     .GroupBy(p => p.Modbus!.Area))
        {
            var areaPoints = areaGroup.ToList();
            var slaveId = areaPoints[0].Modbus!.SlaveId;

            foreach (var block in ReadBlockPlanner.PlanForModbus(
                         areaPoints,
                         _options.MergeWindowRegisters,
                         _options.MaxRegistersPerRequest))
            {
                try
                {
                    var registers = ReadRegisters(_master, (byte)slaveId, areaGroup.Key, block);
                    DecodeBlockInto(block, registers, values);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 规格 5.3 节：一个读块失败不影响同台设备的其他读块，坏点写 NULL
                    IsConnected = false;
                    foreach (var point in block.Points)
                    {
                        values[point] = null;
                    }
                }
            }
        }

        // 未被任何读块覆盖的点（理论上不会发生，防御性处理）也要有条目
        foreach (var point in points.Where(p => !values.ContainsKey(p)))
        {
            values[point] = null;
        }

        return Task.FromResult(new ReadResult(values));
    }

    public void Dispose() => DisposeMaster();

    private static ushort[] ReadRegisters(
        IModbusMaster master,
        byte slaveId,
        ModbusRegisterArea area,
        ReadBlock block) => area switch
        {
            ModbusRegisterArea.HoldingRegister => master.ReadHoldingRegisters(slaveId, (ushort)block.StartAddress, (ushort)block.Length),
            ModbusRegisterArea.InputRegister => master.ReadInputRegisters(slaveId, (ushort)block.StartAddress, (ushort)block.Length),
            _ => throw new NotSupportedException($"寄存器区位 {area} 不是 16 位寄存器读，请用线圈读路径"),
        };

    private static void DecodeBlockInto(
        ReadBlock block,
        ushort[] registers,
        Dictionary<PointConfig, object?> values)
    {
        // 寄存器序列转回大端字节流，之后统一走 ByteDecoder
        var bytes = new byte[registers.Length * 2];

        for (var i = 0; i < registers.Length; i++)
        {
            bytes[i * 2] = (byte)(registers[i] >> 8);
            bytes[i * 2 + 1] = (byte)(registers[i] & 0xFF);
        }

        foreach (var point in block.Points)
        {
            var relativeRegisters = point.Modbus!.RegisterAddress - block.StartAddress;
            var byteOffset = relativeRegisters * 2;

            values[point] = point.DataType == PointDataType.Bool
                ? ByteDecoder.DecodeBool(bytes, byteOffset, point.S7?.BitOffset ?? 0)
                : ByteDecoder.DecodeNumeric(bytes, byteOffset, point.DataType, point.ByteOrder);
        }
    }

    private void DisposeMaster()
    {
        _master?.Dispose();
        _master = null;
        IsConnected = false;
    }
}
```

> **实现提示**：NModbus 3.x 的 API 命名空间是 `NModbus`，`IModbusMaster`、`ModbusFactory` 均在其中。
> 线圈区（`Coil` / `DiscreteInput`）读的是位而非寄存器，需要单独一条路径（`ReadCoils` / `ReadInputs`），
> 位序与 `ByteDecoder.DecodeBool` 的映射必须做一次实测核对——**这是本任务唯一需要真实设备或模拟器确认的地方**。
> 若暂时没有设备，先让 BOOL 点走"读线圈 → 每 8 位打包成 1 字节"的路径，并加一个待现场确认的注释。

- [ ] **Step 5: 运行测试确认通过**

Run:
```powershell
dotnet test tests\PlcDataHub.Protocols.Tests
```
Expected: `Passed!`

- [ ] **Step 6: 提交**

```powershell
git add <本任务改动的显式路径>
git commit -m "feat: Modbus TCP 连接与可编程假连接

- ModbusTcpConnection 按寄存器区分别规划读块，读块级故障隔离
- FakePlcConnection 支持脚本化响应序列，可构造重连/超时故障场景
- 单实例由单线程独占，内部不做任何并发保护（符合规格 5.1）"
```

---

## Task 8：PostgreSQL 连接工厂与结构内省

**Files:**
- Create: `src/PlcDataHub.Storage/PlcDataHub.Storage.csproj`
- Create: `src/PlcDataHub.Storage/NpgsqlConnectionFactory.cs`
- Create: `src/PlcDataHub.Storage/SchemaIntrospector.cs`
- Create: `tests/PlcDataHub.Integration.Tests/PlcDataHub.Integration.Tests.csproj`
- Create: `tests/PlcDataHub.Integration.Tests/TestDatabase.cs`
- Create: `tests/PlcDataHub.Integration.Tests/SchemaIntrospectorTests.cs`

**Interfaces:**
- Consumes: `ExistingTable` / `ExistingColumn`（Task 5）、`MigrationPlanner.DataSchema`（Task 5）
- Produces:
  - `sealed class NpgsqlConnectionFactory(string connectionString)`，方法 `Task<NpgsqlConnection> OpenAsync(CancellationToken)` 与 `string ConnectionString { get; }`
  - `sealed class SchemaIntrospector(NpgsqlConnectionFactory factory)`，方法 `Task<IReadOnlyList<ExistingTable>> ReadDataSchemaAsync(CancellationToken)`
  - `sealed class TestDatabase : IAsyncDisposable` —— 集成测试用，含 `static bool IsAvailable`、`Task<NpgsqlConnection> OpenAsync()`、`Task ResetAsync()`

- [ ] **Step 1: 建立存储项目与集成测试项目**

Run:
```powershell
cd D:\deepseek\PLC_Collect
dotnet new classlib -o src\PlcDataHub.Storage
dotnet new xunit -o tests\PlcDataHub.Integration.Tests --no-restore
dotnet sln add src\PlcDataHub.Storage\PlcDataHub.Storage.csproj tests\PlcDataHub.Integration.Tests\PlcDataHub.Integration.Tests.csproj
dotnet add src\PlcDataHub.Storage reference src\PlcDataHub.Core
dotnet add tests\PlcDataHub.Integration.Tests reference src\PlcDataHub.Storage
dotnet add tests\PlcDataHub.Integration.Tests reference src\PlcDataHub.Core
dotnet add tests\PlcDataHub.Integration.Tests reference src\PlcDataHub.Protocols
dotnet add src\PlcDataHub.Storage package Npgsql
dotnet add tests\PlcDataHub.Integration.Tests package FluentAssertions --version 6.12.1
```

> **同样不要加 `-f net8.0`**（.NET 10 SDK 模板不支持，见 Task 6 Step 3 的实测说明）。
> 然后**删掉两个 csproj 里模板生成的 `<TargetFramework>` 行**（由 `Directory.Build.props` 统一提供），
> 删除模板文件 `Class1.cs` / `UnitTest1.cs`。

- [ ] **Step 2: 写 `TestDatabase`（集成测试的门槛）**

路径：`tests/PlcDataHub.Integration.Tests/TestDatabase.cs`

```csharp
using Npgsql;

namespace PlcDataHub.Integration.Tests;

/// <summary>
/// 集成测试用的真实 PostgreSQL。
/// 环境变量 PLCDATAHUB_TEST_PG 未设置时 <see cref="IsAvailable"/> 为 false，
/// 相关测试会跳过而不是失败——这样没有数据库的机器也能跑通 `dotnet test`。
/// 需要真实库时设置：$env:PLCDATAHUB_TEST_PG = "Host=localhost;Port=5432;Database=plcdatahub_test;Username=postgres;Password=***"
/// </summary>
public sealed class TestDatabase
{
    private const string EnvVarName = "PLCDATAHUB_TEST_PG";

    private readonly string _connectionString;

    public TestDatabase()
    {
        _connectionString = Environment.GetEnvironmentVariable(EnvVarName)
                            ?? string.Empty;
    }

    /// <summary>本机是否配置了测试数据库。</summary>
    public static bool IsAvailable =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVarName));

    public string ConnectionString => _connectionString;

    public async Task<NpgsqlConnection> OpenAsync()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException($"未设置环境变量 {EnvVarName}，无法执行集成测试");
        }

        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>清空 data schema 下的所有表，让每个用例从干净状态开始。</summary>
    public async Task ResetAsync()
    {
        await using var connection = await OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            drop schema if exists d cascade;
            drop schema if exists cfg cascade;
            drop schema if exists rt cascade;
            create schema d;
            create schema cfg;
            create schema rt;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
```

- [ ] **Step 3: 写失败测试**

路径：`tests/PlcDataHub.Integration.Tests/SchemaIntrospectorTests.cs`

```csharp
using FluentAssertions;
using PlcDataHub.Storage;
using Xunit;

namespace PlcDataHub.Integration.Tests;

public class SchemaIntrospectorTests
{
    [SkippableFact]
    public async Task 能读出_d_schema_下的表与列及行数()
    {
        Skip.IfNot(TestDatabase.IsAvailable, "未配置 PLCDATAHUB_TEST_PG");

        var db = new TestDatabase();
        await db.ResetAsync();

        await using (var connection = await db.OpenAsync())
        {
            await using var create = connection.CreateCommand();
            create.CommandText = """
                create table d.plc01_fast (
                    ts timestamp not null,
                    q smallint not null,
                    wen_du double precision
                );
                insert into d.plc01_fast (ts, q, wen_du)
                select now() - (i || ' seconds')::interval, 0, i::double precision
                from generate_series(1, 7) as i;
                """;
            await create.ExecuteNonQueryAsync();
        }

        var factory = new NpgsqlConnectionFactory(db.ConnectionString);
        var introspector = new SchemaIntrospector(factory);

        var tables = await introspector.ReadDataSchemaAsync(CancellationToken.None);

        var table = tables.Should().ContainSingle().Subject;
        table.TableName.Should().Be("plc01_fast");
        table.RowCount.Should().Be(7);
        table.Columns.Select(c => c.Name).Should().BeEquivalentTo("ts", "q", "wen_du");
        table.Columns.Single(c => c.Name == "wen_du").PostgresType.Should().Be("double precision");
    }

    [Fact]
    public async Task 没有_d_schema_时返回空而不是抛异常()
    {
        Skip.IfNot(TestDatabase.IsAvailable, "未配置 PLCDATAHUB_TEST_PG");

        var db = new TestDatabase();
        await using (var connection = await db.OpenAsync())
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = "drop schema if exists d cascade;";
            await drop.ExecuteNonQueryAsync();
        }

        var introspector = new SchemaIntrospector(new NpgsqlConnectionFactory(db.ConnectionString));

        var tables = await introspector.ReadDataSchemaAsync(CancellationToken.None);

        tables.Should().BeEmpty("d schema 不存在时应视为没有任何数据表，交给迁移器去建");
    }
}
```

Run:
```powershell
dotnet test tests\PlcDataHub.Integration.Tests
```
Expected: 编译失败，`SchemaIntrospector` / `NpgsqlConnectionFactory` 未定义。
（`SkippableFact` 需要 `Xunit.SkippableFact` 包，在 Step 4 一起加。）

- [ ] **Step 4: 写实现**

Run:
```powershell
dotnet add tests\PlcDataHub.Integration.Tests package Xunit.SkippableFact
```

路径：`src/PlcDataHub.Storage/NpgsqlConnectionFactory.cs`

```csharp
using Npgsql;

namespace PlcDataHub.Storage;

/// <summary>
/// 数据库连接工厂。刻意不引连接池之外的抽象——
/// 本项目的数据库访问点很少（内省、迁移、写入、查询），过度抽象只会增加理解成本。
/// </summary>
public sealed class NpgsqlConnectionFactory
{
    public NpgsqlConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("连接串不能为空", nameof(connectionString));
        }

        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
```

路径：`src/PlcDataHub.Storage/SchemaIntrospector.cs`

```csharp
using PlcDataHub.Core.Migration;

namespace PlcDataHub.Storage;

/// <summary>
/// 读取数据库中现存的数据表结构，供迁移差异引擎比对。
/// 规格 4.1 节流程的第 2 步。
/// </summary>
public sealed class SchemaIntrospector
{
    private readonly NpgsqlConnectionFactory _factory;

    public SchemaIntrospector(NpgsqlConnectionFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// 读取 schema d 下所有表的列与行数。
    /// d schema 不存在时返回空集合——这表示"还没建过任何数据表"，不是错误。
    /// </summary>
    public async Task<IReadOnlyList<ExistingTable>> ReadDataSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);

        var columnsByTable = new Dictionary<string, List<ExistingColumn>>(StringComparer.Ordinal);
        var rowCounts = new Dictionary<string, long>(StringComparer.Ordinal);

        // 一次查出所有列。data_type 返回的是 PG 内部别名（int4 / timestamp without time zone），
        // 由 MigrationPlanner.TypesEquivalent 负责归一。
        await using (var columnsCommand = connection.CreateCommand())
        {
            columnsCommand.CommandText = """
                select c.table_name, c.column_name, c.data_type
                from information_schema.columns c
                join information_schema.tables t
                  on t.table_schema = c.table_schema and t.table_name = c.table_name
                where c.table_schema = @schema
                  and t.table_type = 'BASE TABLE'
                order by c.table_name, c.ordinal_position
                """;
            columnsCommand.Parameters.AddWithValue("schema", MigrationPlanner.DataSchema);

            await using var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var tableName = reader.GetString(0);
                if (!columnsByTable.TryGetValue(tableName, out var columns))
                {
                    columns = [];
                    columnsByTable[tableName] = columns;
                }

                columns.Add(new ExistingColumn(reader.GetString(1), reader.GetString(2)));
            }
        }

        if (columnsByTable.Count == 0)
        {
            return Array.Empty<ExistingTable>();
        }

        // 行数单独查。必须用 count(*) 精确统计：
        // pg_class.reltuples 在表刚建、尚未 ANALYZE 时返回 -1，而迁移预览要报给用户
        // "将丢弃 N 行"的真实数字。表数量在 5~15 张量级，逐表 count(*) 的开销可接受。
        await using (var countCommand = connection.CreateCommand())
        {
            var tableNames = columnsByTable.Keys.ToList();

            foreach (var tableName in tableNames)
            {
                await using var perTable = connection.CreateCommand();
                perTable.CommandText = $"select count(*) from {MigrationPlanner.DataSchema}.\"{tableName}\"";
                var scalar = await perTable.ExecuteScalarAsync(cancellationToken);
                rowCounts[tableName] = scalar is long value ? value : 0L;
            }
        }

        return columnsByTable
            .Select(kv => new ExistingTable(
                kv.Key,
                kv.Value,
                rowCounts.TryGetValue(kv.Key, out var count) ? count : 0))
            .ToList();
    }
}
```

> **实现提示**：行数用 `count(*)` 精确统计（实现里已经这样写）。
> 不要改回 `pg_class.reltuples`——它在表刚建、尚未 ANALYZE 时返回 **-1**，
> 会让 Step 3 的测试断言 `RowCount == 7` 必然失败，也会让迁移预览把"将丢弃 -1 行"显示给用户。

- [ ] **Step 5: 运行测试**

Run:
```powershell
# 没有数据库时（应全部跳过）：
dotnet test tests\PlcDataHub.Integration.Tests
# 有数据库时（用户已自行安装 PG）：
$env:PLCDATAHUB_TEST_PG = "Host=localhost;Port=5432;Database=plcdatahub_test;Username=postgres;Password=你的密码"
dotnet test tests\PlcDataHub.Integration.Tests
```
Expected: 未设环境变量时报告 skipped、无失败；设了环境变量时 `Passed!`。

- [ ] **Step 6: 提交**

```powershell
git add <本任务改动的显式路径>
git commit -m "feat: PostgreSQL 连接工厂与数据表结构内省

- SchemaIntrospector 读 information_schema 得到现存列与行数
- d schema 不存在时返回空集合而非报错
- 集成测试以环境变量 PLCDATAHUB_TEST_PG 为门槛，未配置则跳过"
```

---

## Plan 1 完成标准

Task 1~8 全部完成后，应当满足：

- [ ] `dotnet build` 全解决方案零警告零错误（`TreatWarningsAsErrors=true`）
- [ ] `dotnet test` 未配置数据库时全绿（集成测试跳过）
- [ ] 配置 `PLCDATAHUB_TEST_PG` 后 `dotnet test` 全绿
- [ ] `git log --oneline` 有 8 个语义化提交

**Plan 1 不产出可运行的采集器**——那是 Plan 2 的目标。Plan 1 的产出是一套经过测试的核心逻辑：
配置模型、列名生成、DDL 生成、迁移差异引擎、读块合并、字节解码、Modbus TCP 连接、数据库内省。

**下一份计划**：`2026-02-09-plc-datahub-plan2.md`
（迁移执行器与集成测试 → 批量写入 → 采集器控制台宿主 → 缓冲与补写 → 端到端验收 A1/A2/A12）
