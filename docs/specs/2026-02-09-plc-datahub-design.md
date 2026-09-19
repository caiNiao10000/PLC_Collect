# 设计规格：PLC DataHub — 多协议 PLC 数据采集与历史趋势系统

- 日期：2026-02-09
- 状态：五节设计已与用户逐节确认，待用户审规格，审后进入实施计划
- 工程目录：`D:\deepseek\plc-datahub`
- 交付形态：两个可执行程序 + PostgreSQL + 本地兜底文件

---

## 1. 背景与目标

工业现场需要一款软件，把多台 PLC 的采集点数据持续采下来、存进数据库，并通过网页组态和查看历史曲线。现场网络与设备会**频繁断线**，软件必须能无限次自动重连、重连成功即恢复工作，且不丢已采数据。

### 1.1 目标

1. 支持西门子 S7 全系（S7-200 SMART / 300 / 400 / 1200 / 1500）与 Modbus TCP / RTU 采集
2. 网页界面完成全部组态：PLC 连接参数、数据点参数、数据库参数
3. 根据配置的采集点**自动生成/迁移数据库表**，表含时间字段 + 每个采集点一列
4. 断线无限重连、退避重试、重连即恢复采集，全程无需人工干预
5. 数据库断线时本地缓冲，恢复后按原始时间戳补写
6. 历史数据曲线展示：多曲线叠加、时间范围选择、缩放平移、游标读数

### 1.2 非目标（本期明确不做）

| 不做 | 说明 |
|---|---|
| 报警/事件系统 | 越限报警、报警确认、报警历史、声音提示、弹窗，全部不做。`rt.status_events` 只记录**通讯与配置类**事件，不是报警系统 |
| 向 PLC 写入 / 下发参数 | 只读采集，不做任何写操作 |
| 对接 MES / ERP / 云平台 / 报表系统 | 只保证本地落库 |
| 统计报表 | 班次平均值、最大值等统计不做，只存原始值 |
| 数据归档 / 自动清理 | 数据只增不删，靠磁盘。运维自行处理 |
| 设备运维监控页 | 只在界面显示连接状态与最后采集时间，不做囤点统计、心跳看板等专门页面 |

### 1.3 已确认的关键约束

| 项 | 值 | 来源 |
|---|---|---|
| 规模 | 2~5 台 PLC，100~500 个采集点，采集周期 1~10 秒 | 用户确认 |
| 数据库 | PostgreSQL，现场机**原生安装服务**，不用 Docker | 用户确认 |
| **目标操作系统** | **Windows 10（1607+）与 Windows 11 双版本支持** | 用户确认 |
| 权限 | 单机单用户，**不做登录** | 用户确认 |
| 验收测试 | 使用**真实 PLC**；单元/状态机测试用假连接 | 用户确认 |

### 1.4 时间字段：数据表与元数据表分别处理

| 表 | 时间类型 | 理由 |
|---|---|---|
| **采集数据表**（`d.*`） | `timestamp`（无时区），存**本地时间** | 用户确认：现场直观优先，直接查库看到的即墙上时间 |
| **配置与状态表**（`cfg.*` / `rt.*`） | `timestamptz`，存 UTC | 元数据表不参与"现场直接查库看数据"；用 `timestamptz` 更稳，开发者读日志无歧义 |

### 1.5 目标操作系统与平台约束（Windows 10 + Windows 11 双支持）

**核心约束：目标框架必须锁定 `net8.0-windows`，不得升级到 net9.0 / net10.0。**

| 版本 | 是否支持 Windows 10 | 结论 |
|---|---|---|
| .NET 8（LTS） | ✅ 支持 Windows 10 版本 1607 及以上，同时支持 Windows 11 | **采用**。这是最后一个支持 Windows 10 的 LTS 版本 |
| .NET 9 | ❌ 已放弃 Windows 10 支持 | 禁止使用 |
| .NET 10 | ❌ 已放弃 Windows 10 支持 | **特别禁止**：本开发机已装有 .NET 10 运行时，极易被顺手升级，一旦升级则 Win10 工控机无法运行 |

> 这条约束必须写进 `Directory.Build.props` 并在 CI/构建脚本中加校验，防止后来者无意升级。工厂存量工控机以 Windows 10 为主，这个错误一旦发生，等到现场部署才暴露，代价极高。

**目标架构：64 位（`win-x64`）**。现场 Windows 10 与 Windows 11 工控机均为 64 位（用户确认），因此**只发布 `win-x64` 一个包**，不做 `win-x86`。

**自包含发布（Self-contained publish）**：两个程序均以 `--self-contained -r win-x64` 发布，**不要求现场机预装 .NET 运行时**。理由：工业现场常是内网离线环境，让运维去装运行时（还可能装错版本）比让发布包大 70 MB 代价高得多。

**版本控制**：`D:\deepseek\plc-datahub` 初始化 git 仓库，代码、规格文档、实施计划、部署脚本全部纳入版本控制（用户确认）。

**Windows 10 / 11 差异清单（实施时必须逐项验证）**

| 差异点 | Windows 10 | Windows 11 | 应对 |
|---|---|---|---|
| PowerShell | 5.1 内置 | 5.1 内置（7.x 可选） | `deploy\*.ps1` 必须兼容 **PowerShell 5.1**，禁止使用 7.x 专有语法；不得依赖 `pwsh` |
| 执行策略 | 默认 `Restricted`（本机已实测被挡） | 默认 `Restricted` | 安装脚本必须以 `powershell -ExecutionPolicy Bypass -File` 方式调用，不能假设已放开策略 |
| Windows 服务安装 | `sc.exe create` / `sc.exe failure` | 同左 | 用两版通用命令；不用 Windows 11 专属的服务新特性 |
| 串口驱动（Modbus RTU） | 依赖 USB 转串口芯片厂商驱动 | 同左，但部分新芯片驱动在 Win10 上需手动装 | 部署文档必须写明：**先确认设备管理器里 COM 口正常，再配 Modbus RTU**；这是现场最常见的"软件没问题但采不到数据"原因 |
| 系统自带 PostgreSQL 依赖 | 无 | 无 | PG 原生安装包两版通用 |
| 长路径支持 | 默认关闭 | 默认关闭 | 不用超过 260 字符的路径；发布目录避免深层嵌套 |
| DPAPI 加密配置文件密码 | 支持（当前用户/本机作用域） | 同左 | 用 `LocalMachine` 作用域，因为采集器是**服务账号**运行，不是登录用户 |
| 高 DPI 缩放 | 常见 125%~150% | 常见 150% | Blazor 页面用响应式布局，不写死像素尺寸 |

**验收补充项**：A1~A15 全部需要在 **Windows 10 与 Windows 11 上各跑一遍**（A14 是专门的跨系统验收项），特别是服务安装/卸载、串口采集、以及自包含发布包在无 .NET 运行时的干净系统上能直接运行。

---

## 2. 总体架构

### 2.1 双进程 + 共享状态

采用**方案 B：双进程**。浏览器端 Blazor Server 的 WebSocket 会话是不可信负载（终端网络抖动、误刷新），与采集同进程共享 GC 与线程池会导致采集不稳定。进程边界是唯一可靠的隔离手段。

```
┌──────────────────────────┐         ┌──────────────────────────┐
│  PlcDataHub.Collector    │         │  PlcDataHub.Web          │
│  Windows 服务 (无 Web)    │         │  Windows 服务 + Kestrel   │
│                          │         │  Blazor Server           │
│  • 采集线程池(每连接一条)  │         │  • 组态界面(连接/组/点)    │
│  • 重连与退避            │         │  • 库配置界面             │
│  • 读块合并 / 类型解码    │         │  • 历史曲线(后端降采样)    │
│  • 本地缓冲队列           │         │  • 状态展示(轮询+NOTIFY)   │
│  • 表结构迁移执行         │         │  • 迁移预览与确认          │
└───────┬──────────┬───────┘         └───────┬──────────┬───────┘
        │          │                         │          │
        │ 采集数据  │ 配置/状态               │ 配置读写  │ 状态读
        ▼          ▼                         ▼          ▼
   ┌────────────────────────────────────────────────────────┐
   │              PostgreSQL（唯一的共享真相源）              │
   │  cfg.*  配置    rt.*  运行状态/事件    d.*  采集数据      │
   └────────────────────────────────────────────────────────┘
        │                                          │
        │ PG 不可用时                               │
        ▼                                          ▲
   config.snapshot.json  ←── 启动兜底 ──►  Web 提示"库不可用"
   buffer/pending-*.jsonl ←── 采集缓冲，恢复后补写
```

### 2.2 进程职责边界

**采集器（PlcDataHub.Collector）负责**：建立与维持 PLC 连接、周期采集、读块合并、类型解码、数据缓冲、写入 PostgreSQL、执行表结构迁移、维护 `rt.*` 运行状态、响应配置热加载。**不做**降采样、不做曲线、不管用户界面、绝不写 PLC。

**Web 界面（PlcDataHub.Web）负责**：配置的增删改查、迁移差异预览与确认触发、历史数据查询与降采样、状态展示、数据库连接参数管理。**不碰** PLC、不直接采集。

### 2.3 两个进程之间的接口（仅两样）

| 接口 | 方向 | 机制 |
|---|---|---|
| 配置变更通知 | PG → 采集器 | `cfg.config_revision` 更新时触发器 `NOTIFY dsh_config_changed`；采集器收到即重读配置热重建采集组。**同时每 10 秒轮询一次版本号作为兜底**（NOTIFY 在连接中断时会丢） |
| 运行状态 | 采集器 → PG → Web | 采集器每 5 秒把状态 UPSERT 到 `rt.group_status`；状态**变化**时追加 `rt.status_events` 并 `NOTIFY dsh_runtime`，Web 收到即刷新，否则按 5 秒轮询 |

### 2.4 本地兜底文件（不是真相源）

| 文件 | 内容 | 用途 |
|---|---|---|
| `config/appsettings.json` | **仅数据库连接参数**（host/port/database/user/password，密码用 DPAPI 加密存储） | 引导配置。因为"配置存 PG"这件事本身需要先知道 PG 在哪，此环只能由本地文件打破 |
| `config/config.snapshot.json` | 全量配置图 + `config_revision` | ① PG 连不上时用快照启动采集（**断库不停采**）；② 采集器重连 PG 后比对版本号决定是否热加载 |
| `buffer/pending-{utc}.jsonl` | 缓冲的采集数据（追加写） | 库断线时落盘，恢复后按原始时间戳补写 |

### 2.5 已知取舍（明确接受）

- **PG 断线期间无法修改配置**。表结构迁移必须连库才能执行，因此断库时界面只能看不能改，PG 恢复后自动恢复可用。
- **PG 断线期间 `rt.group_status` 无法更新**。界面显示的是最后一次已知状态，并标注"数据可能不是最新"。

---

## 3. 配置数据模型

### 3.1 三层模型

```
设备连接 DeviceConnection            例：1#窑 PLC
├─ 协议：S7 / Modbus TCP / Modbus RTU
├─ S7 参数：IP + 机架 + 槽 + 本地TSAP + 远程TSAP
├─ Modbus TCP 参数：IP + 端口
├─ Modbus RTU 参数：串口号 + 波特率 + 数据位 + 停止位 + 校验
└─ 采集组 PollGroup × N              例：快组 1s → 表 d.plc01_fast
   └─ 采集点 Point × N               例：1#窑尾温度
```

**同一连接下的多个采集组共用一条长连接**（S7 可在一次连接上并发多个读请求，周期不同互不干扰）。

### 3.2 `cfg` — 配置层

```sql
cfg.device_connection (
    conn_id        serial primary key,
    conn_code      text not null unique,     -- 设备代码，用于生成表名，仅 [a-z0-9_]
    conn_name      text not null,            -- 显示名，可中文
    protocol       text not null,            -- S7 | MODBUS_TCP | MODBUS_RTU
    -- S7 / Modbus TCP
    host           text,
    port           integer,                  -- 默认 102(S7) / 502(Modbus TCP)
    rack           integer,                  -- S7: 默认 0
    slot           integer,                  -- S7: 200SMART/300/400=2, 1200/1500=1
    tsap_local     text,                     -- S7: 留空自动
    tsap_remote    text,                     -- S7: 留空自动
    -- Modbus RTU
    serial_port    text,                     -- 如 COM3
    baud           integer,                  -- 9600 / 19200 / 38400 / 115200
    databits       integer, stopbits integer, parity text,
    -- 通用
    enabled        boolean not null default true,
    sort_order     integer not null default 0,
    remark         text,
    created_at     timestamptz not null default now(),
    updated_at     timestamptz not null default now()
);

cfg.poll_group (
    group_id       serial primary key,
    conn_id        integer not null references cfg.device_connection(conn_id) on delete cascade,
    group_code     text not null unique,     -- 组代码，仅 [a-z0-9_]
    group_name     text not null,            -- 显示名，可中文
    period_ms      integer not null,         -- 采集周期，1000 ~ 600000
    table_name     text not null,            -- 目标表名（不含 schema），默认 d.{conn_code}_{group_code}
    enabled        boolean not null default true,
    sort_order     integer not null default 0,
    remark         text,
    created_at     timestamptz not null default now(),
    updated_at     timestamptz not null default now()
);

cfg.point (
    point_id       serial primary key,
    group_id       integer not null references cfg.poll_group(group_id) on delete cascade,
    point_code     text not null unique,     -- 点代码
    point_name     text not null,            -- 显示名，可中文（界面/导出表头）
    column_name    text not null,            -- 库内列名，全 ASCII，留空则自动生成
    unit           text,                     -- 工程单位，仅展示
    data_type      text not null,            -- BOOL|BYTE|WORD|DWORD|SINT|USINT|INT|UINT|DINT|UDINT|REAL|LREAL|STRING|DTL
    byte_order     text not null default 'BIG',  -- BIG|LITTLE（仅多字节类型生效）
    scale          double precision not null default 1,
    "offset"       double precision not null default 0,
    decimals       integer,                  -- 展示小数位
    enabled        boolean not null default true,
    sort_order     integer not null default 0,
    -- S7 专属
    s7_area        text,                     -- I|Q|M|DB
    s7_db          integer,                  -- DB 号（S7-200 SMART 的 V 区 = DB1）
    s7_byte        integer,                  -- 字节偏移
    s7_bit         integer,                  -- 位偏移 0~7（BOOL 用）
    -- Modbus 专属
    mb_slave       integer,                  -- 从站号 1~247
    mb_reg_area    text,                     -- COIL|DISCRETE|HOLDING|INPUT
    mb_address     integer,                  -- 寄存器地址（0 基）
    created_at     timestamptz not null default now(),
    updated_at     timestamptz not null default now()
);

cfg.config_revision (
    id             integer primary key default 1 check (id = 1),
    revision       bigint not null default 0,
    changed_at     timestamptz not null default now(),
    changed_by     text
);
-- 变更触发器：revision++ ; NOTIFY dsh_config_changed
-- cfg.device_connection / cfg.poll_group / cfg.point 上的 AFTER INSERT/UPDATE/DELETE 都触发
```

**校验规则（在 Web 端与迁移器双重执行）**：
- `conn_code` / `group_code` 仅允许 `[a-z0-9_]`，长度 ≤ 20
- 协议专属字段必填性随 `protocol` 变化：S7 必填 `host`；Modbus TCP 必填 `host`+`port`；Modbus RTU 必填 `serial_port`+`baud`
- 采集点地址字段必填性随父连接协议变化
- `data_type=BOOL` 时 S7 必须填 `s7_bit`；Modbus 只允许 `COIL`/`DISCRETE`
- 同一组内 `column_name` 唯一（对应表中列唯一）

### 3.3 `rt` — 运行状态层（仅采集器写）

```sql
rt.group_status (
    group_id            integer primary key references cfg.poll_group(group_id) on delete cascade,
    conn_state          text not null,      -- CONNECTED | CONNECTING | DISCONNECTED | BACKOFF | UNAVAILABLE | DISABLED
    conn_lost_since     timestamptz,        -- 本次断线起始（用于界面显示"已断 12 分钟"）
    last_ok_ts          timestamptz,        -- 最后一次采集成功
    last_err_ts         timestamptz,
    last_error          text,
    consecutive_errors  integer not null default 0,
    reconnect_count     bigint not null default 0,   -- 累计重连成功次数
    points_total        integer, points_bad integer,
    samples_total       bigint, sample_rate_per_min double precision,
    roundtrip_ms        integer,            -- 最近一轮采集耗时
    buffer_backlog      bigint,             -- 当前缓冲积压行数
    db_state            text,               -- CONNECTED | DISCONNECTED
    updated_at          timestamptz not null default now()
);

rt.status_events (
    event_id       bigserial primary key,
    conn_id        integer, group_id integer, point_id integer,
    event_type     text not null,
    -- conn_lost | conn_restored | db_lost | db_restored
    -- poll_overrun | point_bad | point_recovered | config_reloaded
    -- migration_applied | buffer_overflow | collector_started | collector_stopped
    detail         jsonb,
    ts             timestamptz not null default now()
);
create index on rt.status_events (ts desc);
create index on rt.status_events (group_id, event_type, ts desc);
```

**断线区间的定义**：`conn_lost` 与配对的 `conn_restored` 构成一个 `[start, end)` 区间，曲线图据此绘制阴影。未配对的 `conn_lost` 表示"当前仍在断线中"，阴影画到当前时间。

### 3.4 `d` — 数据层（每组一张宽表，由迁移器创建）

```sql
create table d.plc01_fast (
    ts          timestamp        not null,          -- 采集时刻，本地时间
    q           smallint         not null default 0,-- 行质量：0=好 1=部分坏点(存在NULL) 2=补写
    temp_kiln   double precision,                   -- 每个采集点一列，类型由 data_type 映射
    pres_fan    double precision,
    run_main    boolean,
    src_ts      timestamp                           -- 仅补写行的非空列；正常直写行为 NULL
);
create index on d.plc01_fast (ts);
create index on d.plc01_fast (src_ts) where src_ts is not null;   -- 仅索引补写行，体积可忽略
```

> `src_ts` 的用途是**可审计性**：把 `src_ts` 与 `ts` 一起查，可以看出这一行是"当时就写进去的"还是"事后补写的"。
> 行质量 `q` 已经提供了同等信息（直写=0/1，补写=2），`src_ts` 是给"排障时想精确定位原始采样时刻"留的冗余字段。
> 查询与曲线绘制**一律用 `ts`**，`src_ts` 不参与任何查询路径——这样补写数据的 `ts` 就是原始采集时刻，曲线时间轴天然正确。

**明确的设计取舍**：

| 取舍 | 决定 | 理由 |
|---|---|---|
| 主键 / 唯一约束 | **不加**。只有 `ts` 普通索引 | 补写重复概率极低；唯一约束会拖慢批量写入，且宽表 PK 索引体积大 |
| 行语义 | 一行 = 该组**一次采集回合**，该回合所有**成功读到**的点写在同一行，时间戳完全相同 | 采集周期 1~10s，这是唯一能让同一回合各曲线在时间轴上对齐的语义 |
| 类型映射 | `BOOL→boolean`、`BYTE/WORD/DWORD→integer`、`SINT/USINT/INT/UINT/DINT/UDINT→integer`、`REAL/LREAL→double precision`、`STRING→text`、`DTL→timestamp` | `REAL→double precision` 保证不丢精度 |
| 坏点表达 | 该列写 `NULL`，同行使 `q=1` | 与"断线不写行"区分：NULL=采了但没读到；**缺行=没采** |
| 缩放应用位置 | 落库**前**应用 `scale`/`offset`，库里存**工程值** | 曲线与导出直接可用；不保留原始值（用户已确认不需要） |
| 量程校验 | **不做**。`scale`/`offset` 纯粹是工程单位换算工具 | 用户确认 |
| 位地址 | 一个位一列，类型 `boolean`，不打包成位掩码 | 用户确认：直观、画阶梯图直接 |

### 3.5 列名规范化规则

存储**双名**：`point_name`（中文显示名，界面与 CSV 表头用）+ `column_name`（库内列名，SQL 用）。两者均可手工修改；`column_name` 留空则按规则从 `point_name` 自动生成。

| 输入显示名 | 生成列名 | 规则 |
|---|---|---|
| `Temp Kiln` | `temp_kiln` | 字母数字保留，其余转下划线，转小写 |
| `1#窑尾温度` | `p1_yao_wei_wen_du` | 含中文 → 拼音；数字开头加前缀 `p` |
| `温度` | `wen_du` | 纯中文 → 全拼 |
| `PID_输出%` | `pid_shu_chu` | 字母保留，中文转拼音，特殊字符转下划线 |
| 重名冲突 | `wen_du_2`, `wen_du_3` | 自动追加序号 |
| 超过 63 **字节** | 截断到 54 字符 + `_` + 8 位哈希 | PostgreSQL 标识符硬上限 63 字节 |

**列名统一使用 ASCII**：PG 标识符上限是 63 字节（不是字符），中文列名会踩上限、且 psql / Excel 导出易乱码。

---

## 4. 表结构自动迁移

### 4.1 流程

```
界面【保存】配置 ──► 只写 cfg.* 配置表（不动任何表结构）
        │
        ▼
界面【应用表结构变更】──► 迁移引擎
        │
        ├─ 1. 期望结构：由 cfg.* 算出目标 {表名 → 列集合(列名, 类型, 是否允许NULL)}
        ├─ 2. 现有结构：读 information_schema.columns
        ├─ 3. 生成差异计划（不执行）→ 返回预览
        ▼
迁移预览弹窗：逐条列出 +/-/~ 操作、破坏性操作标注受影响行数
        │  [复制 SQL]  [取消]  [执行]
        ▼
用户点【执行】──► 单个事务内执行全部语句；失败整体回滚
        │          执行前后对相关表 ANALYZE
        └─ 写 rt.status_events(migration_applied, detail=执行的SQL+操作者)
```

### 4.2 迁移规则（原则：绝不静默丢数据）

| 场景 | 处理 |
|---|---|
| 新增采集点 | `ALTER TABLE ADD COLUMN`；历史行该列为 NULL |
| 采集点改类型 | **不自动改**。检测到类型冲突即**报错阻止执行**，提示用户二选一："新建一列"或"确认丢弃该列数据后重建" |
| 删除采集点 | 默认 `ALTER TABLE RENAME COLUMN` 为 `deleted_{截断至40字符的列名}_{yyyymmdd}` **软删除保留数据**（总长受 63 字节上限约束）；预览弹窗中可勾选"彻底删除该列" |
| 修改列名 | `ALTER TABLE RENAME COLUMN`，数据不丢 |
| 新增采集组 | `CREATE TABLE` + `CREATE INDEX` |
| 删除采集组 | 默认只停采集，**不删表**；预览弹窗中可勾选"连表一起删除"（二次确认） |
| 表不存在 | 视为全部新增，走 `CREATE TABLE` |
| 执行原子性 | 全部语句在**一个事务**内执行，任一失败整体回滚 |
| 执行后 | 对改动表执行 `ANALYZE`，并写 `rt.status_events` 审计 |

### 4.3 幂等性要求

迁移器必须**幂等**：同一份配置连续执行两次，第二次生成的差异计划为空、不产生任何 DDL。这是实施计划中必须专门测试的一条。

---

## 5. 采集引擎

### 5.1 线程模型

**一条设备连接 = 一条采集线程，独占该 socket / 串口，绝不并发访问。**

S7 与 Modbus RTU 的帧都不带请求 ID，多线程共用一个 socket/串口会串帧，这是现场最难排查的一类缺陷，从架构上直接杜绝。

线程内按组调度：每组维护独立的 `next_due` 时间戳，线程睡到最近到期时刻。**落后就跳过，不补偿堆积**——若一轮采集耗时超过周期，直接开始下一轮，而不是连发多轮打爆 PLC，并记 `poll_overrun` 事件。

### 5.2 读块合并

不合并时 200 个点 = 200 次请求，1 秒周期下任何 PLC 都扛不住。合并后 200 个点通常只需 5~15 次请求。

```
点: DB1.DBD0(r) DB1.DBD4(r) DB1.DBD8(r) ... DB1.DBD40(r)
    └────────────── 合并成 1 次读: DB1.DBD0 LEN 44 ──────────────┘
点: DB1.DBX100.0(bool)   M10.3(bool)   ← 地址不连续 → 2 次读
```

| 协议 | 合并窗口 | 单次上限 |
|---|---|---|
| S7 | 相邻 10 字节内合并 | 受 PDU 协商值限制：连接建立后**自动探测**可用 PDU 长度并缓存，写入请求长度以探测值为准；探测失败则保守回退 240 字节 |
| Modbus TCP | 相邻 16 寄存器内合并 | 120 寄存器 |
| Modbus RTU | 相邻 8 寄存器内合并 | 120 寄存器 |

地址不连续时允许留空洞（多读若干字节）换取更少的请求次数。

### 5.3 连接状态机

```
        ┌─────────────┐
        │  DISABLED   │◄── 配置 enabled=false
        └─────────────┘
               │ 启用
               ▼
        ┌─────────────┐   成功    ┌─────────────┐
   ┌───►│ CONNECTING  │─────────►│  CONNECTED  │
   │    └─────────────┘          └──────┬──────┘
   │           ▲                        │ 读失败/超时/对端断开
   │           │                        ▼
   │    ┌──────┴──────┐          ┌─────────────┐
   │    │   BACKOFF   │◄─────────│DISCONNECTED │
   │    │1→2→4→8→…30s │          └─────────────┘
   │    └─────────────┘                 │ 连续失败 > max_retry(默认5)
   │           │                        ▼
   └───────────┴──────────────────►┌─────────────┐
        永不放弃，永远重试           │ UNAVAILABLE │ 界面标红，
                                    └─────────────┘ 仍按 30s 上限重试
```

**硬性要求**：
- 重连**永不放弃**，不存在"重试 N 次后停止"的终点
- 退避上限默认 30 秒（可配 `reconnect_max_delay_ms`）
- 重连成功后**立即恢复采集**，无需人工干预，且不重新读配置（配置由版本号独立驱动）
- 单轮采集失败只影响本轮：坏点写 NULL，不影响同组其他点、不影响其他组、不影响其他连接
- 重连成功/失败均写 `rt.status_events`，界面可看出"该设备过去 24 小时断过 7 次、最长断了 12 分钟"

### 5.4 数据流：内存队列 + 磁盘溢写 + 恢复补写

```
采集线程 ──► 写入管道（单条后台线程 + Channel<DataRow>）
                │
                ├─ PG 正常 ──────► 批量 INSERT（默认攒 500 行 或 1 秒，先到先发）
                │
                └─ PG 不可用 ────► buffer/pending-{utc}.jsonl（追加落盘，进程重启不丢）
                                        │
                              PG 恢复 ──┴──► 按文件顺序补写，用 src_ts 原始时间戳，q=2
```

| 参数 | 默认值 | 说明 |
|---|---|---|
| 批量攒批 | 500 行 或 1 秒 | 小规模下等效于"每秒一批" |
| 缓冲上限 | 100 万行 **或** 500 MB **或** 7 天 | 三者先到为先 |
| 超限策略 | **丢最旧 + 写 `buffer_overflow` 事件 + 界面告警** | 宁可丢最旧，也不把工控机磁盘写爆 |
| 补写批次 | 每批 2000 行，写一批删一批 | 中途再次断库不会重复补写 |
| 乱序容忍 | 补写数据时间戳早于已有数据 | 表无唯一约束，不会冲突；查询靠 `ts` 索引范围扫描 |
| 补写收尾 | 补写完成后对相关表执行 `ANALYZE` | 补写旧时间戳会让统计信息失真，导致查询选错执行计划；多数同类软件漏掉这一步 |

### 5.5 配置热加载

采集器轮询到 `config_revision` 变化（或收到 `NOTIFY`）后：
1. 从 PG 重读全量配置 → 写 `config.snapshot.json`
2. 与当前运行态做差异比对：**新增连接**→起新线程；**删除/停用连接**→优雅停线程并释放 socket；**连接参数变更**→重建连接；**组周期/表名变更**→重建调度；**点增删改**→重建该连接的读块计划
3. 写 `rt.status_events(config_reloaded)`

**注意**：热加载**不触碰表结构**。表结构只由用户在 Web 端确认执行迁移时改变。

### 5.6 采集器自身健壮性

- 采集线程内层 `try/catch` 兜底，**绝不让异常逃出线程**导致进程退出
- Windows 服务配置失败自动重启（`sc failure`）
- 每 5 秒更新 `rt.group_status` 心跳；Web 端发现 `updated_at` 超过 30 秒即标记"采集器异常"

---

## 6. 界面（Blazor Server）

```
┌─ 顶部导航 ───────────────────────────────────────────────┐
│  📊 实时监视  │  📈 历史曲线  │  ⚙ 系统配置  │  🗄 数据库  │
└──────────────────────────────────────────────────────────┘
```

| 页面 | 内容 |
|---|---|
| **实时监视** | 设备卡片：连接状态灯、已断线时长、最后采集时间、采集速率、往返耗时、缓冲积压；组内采集点当前值与质量（正常 / NULL坏点 / 未采） |
| **历史曲线** | 左：设备▸组▸点 三级勾选树（支持跨 PLC 多选）；右：时间范围（最近 1h / 8h / 24h / 7 天 / 自定义）、曲线区（缩放、平移、游标竖线读数、图例、**断线区间阴影**）、底部降采样提示与"只看原始点"开关、CSV 导出 |
| **系统配置** | 设备连接（增删改 + **连接测试**按钮）▸ 采集组（周期、目标表名）▸ 采集点（表格批量编辑，地址字段随父连接协议自动切换表单）；【应用表结构变更】为迁移预览入口 |
| **数据库** | 连接参数 + 【测试连接】+【创建数据库】；写入参数（攒批行数/间隔）；缓冲参数（上限、超限策略）；**表结构变更历史**（审计） |

**曲线查询策略**：时间范围 ≤ 6 小时直接查原始行；更长的范围由后端做时间桶聚合 / LTTB 降采样，返回点数上限 5000，界面明确标注"已抽稀 N 倍"。

**断线阴影数据来源**：查询时间范围内的 `rt.status_events` 中 `conn_lost` / `conn_restored` 配对区间。

---

## 7. 测试策略

| 层 | 测什么 | 怎么测 |
|---|---|---|
| 单元测试 | 读块合并算法、S7/Modbus 字节解码（大小端、REAL、DTL、STRING）、列名拼音生成与冲突避让、迁移差异引擎、退避计算、时间桶降采样 | 纯函数，不需要 PLC 与数据库，覆盖要密 |
| 状态机测试 | 连接状态机全路径：连接→断开→退避→重连→恢复采集；含"退避期间配置变更""重连后中间态"等边界 | 实现 `IFakePlcConnection`，按脚本返回超时/异常/正常数据，可精确构造现场故障序列 |
| 集成测试 | 真实 PG 上的迁移（建表/加列/软删除/类型冲突拦截/幂等）、批量写入、缓冲补写、`NOTIFY` 触发、`ANALYZE` | 独立测试 database，每用例重建 schema |
| 真实设备联调 | 真实 PLC 上验证 S7 与 Modbus 采集、读块合并效果、断线重连 | 用户现场真实 PLC |
| 长跑 | 连续 72 小时，观察内存/句柄/磁盘是否缓慢增长，捕捉偶发断线 | 现场或实验室机器 + 日志分析 |

### 7.1 验收标准

| # | 验收项 | 判定标准 |
|---|---|---|
| A1 | 自动建表 | 配好 3 台设备 / 6 个采集组 / 200 个点 → 预览确认后出现 6 张宽表，列名与类型与配置一一对应 |
| A2 | 采集频率 | 1s 组连续 10 分钟，行数 = 600 ± 5%，同一行内各点时间戳完全一致 |
| A3 | PLC 断线重连 | 断网 60 秒后恢复 → **≤ 35 秒内自动恢复采集**，无需人工干预；`rt.status_events` 有配对的 `conn_lost`/`conn_restored`；曲线出现对应阴影 |
| A4 | 库断线补写 | 停 PG 2 分钟（PLC 正常采）→ 启动 PG → 缓冲数据**全部补写成功**，`src_ts` 为原始采集时间、`q=2`；补写后已执行 `ANALYZE` |
| A5 | 缓冲上限 | 人为令缓冲超限 → 丢最旧、界面告警、**磁盘停止增长** |
| A6 | 运行中加采集点 | 采集不停的前提下新增 5 个点并确认迁移 → 出现新列，历史行该列为 NULL，**原有数据一行不少** |
| A7 | 删除采集点 | 删 1 点 → 列被改名为 `deleted_*` 且数据保留，采集正常，界面不再显示该点 |
| A8 | 类型冲突拦截 | 把某点由 `real` 改为 `bool` → 迁移预览**明确报错并阻止执行**，库中数据不变 |
| A9 | 曲线性能 | 查 200 点 / 7 天（约 120 万行）→ 降采样后返回 ≤ 5000 点，**响应 < 3 秒**，浏览器不卡 |
| A10 | 界面可用性 | 断网时界面仍显示"最后一次已知状态"并明确标注，恢复后 5 秒内刷新，不白屏 |
| A11 | 采集器崩溃恢复 | 手工杀掉采集器进程 → Windows 服务自动重启 → 自动继续采集，已采数据不丢（缓冲落盘生效） |
| A12 | 迁移幂等 | 同一份配置连续执行两次迁移 → 第二次差异计划为空、不产生任何 DDL |
| A13 | **目标框架锁定** | 全工程所有 csproj 的 `TargetFramework` 均为 `net8.0-windows`；`publish.ps1` 发现其他值即报错中止 |
| A14 | **Win10 + Win11 双系统** | 在 **Windows 10（1607+）与 Windows 11 各一台干净机器**上：自包含发布包无需预装 .NET 运行时即可启动；服务安装/卸载脚本用 PowerShell 5.1 可执行成功；Modbus RTU 串口采集正常 |
| A15 | 服务账号权限 | 采集器以服务账号运行时，能读写配置文件（DPAPI `LocalMachine` 作用域）、能访问串口、能连接 PG |

---

## 8. 工程结构

```
D:\deepseek\plc-datahub\
├─ PlcDataHub.sln
├─ src\
│  ├─ PlcDataHub.Core\            领域模型、配置 DTO、列名生成、迁移差异引擎、降采样算法（无外部依赖，可纯单元测试）
│  ├─ PlcDataHub.Protocols\       协议抽象 IPlcConnection + S7 实现 + Modbus 实现 + 假连接实现
│  ├─ PlcDataHub.Storage\         Npgsql 访问、批量写入、缓冲与补写、迁移执行器
│  ├─ PlcDataHub.Collector\       Windows 服务宿主、采集线程、调度、状态上报
│  └─ PlcDataHub.Web\             ASP.NET Core + Blazor Server、组态页面、曲线、REST API
├─ tests\
│  ├─ PlcDataHub.Core.Tests\
│  ├─ PlcDataHub.Protocols.Tests\
│  └─ PlcDataHub.Integration.Tests\   需要真实 PG
├─ deploy\
│  ├─ install-service.ps1          安装两个 Windows 服务（必须兼容 PowerShell 5.1）
│  ├─ uninstall-service.ps1
│  ├─ publish.ps1                  自包含发布 + 目标框架校验（见下）
│  └─ db\init.sql                  创建 role / database / schema
├─ Directory.Build.props           ★ 全工程锁定 <TargetFramework>net8.0-windows</TargetFramework>
│                                    与 RuntimeIdentifier=win-x64，防止被升级到 net9/net10
└─ docs\
   ├─ specs\2026-02-09-plc-datahub-design.md    ← 本文档
   └─ deploy-notes.md                            现场部署步骤（含 PG 原生安装、串口驱动检查、双系统验证清单）
```

**目标框架防升级机制**（对应 1.5 节的核心约束，三处同时设防）：

1. `Directory.Build.props` 里统一声明 `<TargetFramework>net8.0-windows</TargetFramework>`，各 csproj **不得单独覆盖**
2. `deploy\publish.ps1` 在发布前读取所有 csproj 的 `TargetFramework`，发现非 `net8.0-windows` 即**报错中止发布**
3. 验收项 A13（见 7.1 节）专门检查这一点

### 8.1 依赖风险与选型复核（实施第一步必须先验证）

本机开发环境已探明：**无 .NET SDK、无 PostgreSQL、无 Docker**（用户自行安装）。以下两个依赖的 .NET 8 兼容性存在不确定性，**实施计划的第一批任务必须是"建空工程 + 装包 + 跑通最小样例"**，不允许在未验证的情况下铺开业务代码：

| 依赖 | 风险 | 对策 |
|---|---|---|
| **NModbus** | 主流版本的历史目标框架为 .NET Standard 2.0 / .NET Framework 代际，在 .NET 8 上的**串口（RTU）路径**需要实测。历史上有 `NModbus4` 分支，但已停止维护，不作为候选 | 第一步写最小样例实测 TCP 与 RTU 两条通道；若 RTU 在 .NET 8 上不可用，退路是自己实现 Modbus RTU 帧层（协议简单，仅 CRC + 功能码 01/02/03/04，工作量可控）——**这也是把 `PlcDataHub.Protocols` 独立成项目的原因** |
| **拼音库** | `TinyPinyin`、`NPinyin` 等包活跃度低，目标框架可能停留在 .NET Framework。若引入失败，**不能因此阻塞** | 首选现代活跃的 `ToolGood.Words.FirstPinyin`；若全部不可用，降级方案是**只做 ASCII 规范化 + 中文列名由用户手工填写或自动用 `point_code`**（见下） |
| **S7.Net** | 工作区已有源码 `D:\deepseek\s7netplus-src\s7netplus-main`，可直接引用或按需修补 | 编译该源码作为本地项目引用，避免包版本不可控；同时便于针对 S7-200 SMART 打补丁 |

**列名生成的降级链**（保证任何情况下都有确定结果，且不阻塞开工）：
```
1. 用户手填 column_name            → 直接用（校验 ASCII + 唯一 + ≤63 字节）
2. point_code 形如合法 ASCII 标识符 → 用 point_code
3. point_name 能生成拼音（拼音库可用）→ 用拼音
4. 拼音库不可用                     → 用 col_{point_id}，并在界面提示用户手工改名
```
第 4 条保证**即使一个拼音库都装不上，软件依然能建表和采集**，只是列名不好看。这比"卡在依赖上"可接受得多。

### 8.2 最小依赖清单

| 用途 | 包 |
|---|---|
|---|---|
| S7 通讯 | `S7.Net`（工作区已有源码 `D:\deepseek\s7netplus-src`，可参考/按需修补） |
| Modbus | `NModbus` |
| PostgreSQL | `Npgsql` |
| 串口 | `System.IO.Ports` |
| 日志 | `Serilog` + `Serilog.Sinks.File`（按大小滚动，保留 N 天） |
| 前台图表 | ECharts（通过 JS 互操作嵌入 Blazor） |
| 拼音 | `ToolGood.Words.FirstPinyin`（首选，活跃度高）；备选 `TinyPinyin`；均不可用时走 8.1 节降级链 |
| 测试 | xUnit + FluentAssertions |

---

## 9. 风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| **目标框架被误升级到 net9/net10** | **Windows 10 工控机完全无法运行，且只在现场部署时才暴露** | `Directory.Build.props` 统一锁定 `net8.0-windows`；`publish.ps1` 发布前校验并中止；验收项 A13 专测；本机装有 .NET 10 运行时，此风险实际存在 |
| **Win10 与 Win11 行为差异** | 某系统上装不上服务 / 串口不工作 | 1.5 节列出 8 项差异清单；安装脚本强制兼容 PowerShell 5.1；A14 在双系统各验一遍 |
| **服务账号权限不足** | 服务启动了但不干活（读不到配置 / 打不开串口 / 连不上库） | A15 专项验收；DPAPI 用 `LocalMachine` 作用域；部署文档写明串口权限与 PG 连接账号要求 |
| S7.Net 对 S7-200 SMART 的兼容性 | 采不到数据 | S7-200 SMART 的 V 区按 DB1 处理；本机 TIA Portal V21 可用；现场有真实 PLC 可立即验证；必要时直接修补工作区已有的 S7.Net 源码 |
| 读块合并读回多余字节导致解码错位 | 数据错但不报错（最危险） | 合并只影响**读取范围**，解码严格按点位偏移在缓冲区中定位；单元测试必须覆盖"空洞边界""跨字节""跨请求边界" |
| 补写旧时间戳后查询计划劣化 | 曲线查询变慢 | 补写完成后强制 `ANALYZE`（已写入设计） |
| 迁移误操作丢数据 | 生产数据损失 | 变更预览强制确认、删点默认软删除、类型冲突硬拦截、单事务执行、全程审计 |
| Blazor Server 断线重连（浏览器侧） | 界面卡顿 | 与采集器进程隔离（本设计核心）；界面断线时显示最后已知状态而非白屏 |
| 工控机磁盘被缓冲写满 | 系统不可用 | 缓冲三重量化上限（行数/体积/时间），超限丢最旧并告警 |
| 列名撞 63 字节上限 | 建表失败 | 统一 ASCII + 超长截断加哈希，生成后校验 |
| USB 转串口驱动缺失（Modbus RTU） | 软件正常但采不到数据，现场排查耗时 | 部署文档把"先确认设备管理器 COM 口正常"列为 RTU 接入前置步骤 |

---

## 10. 遗留待定项（不阻塞开工）

| 项 | 当前默认 | 何时需要定 |
|---|---|---|
| 补写乱序数据的查询表现 | 依赖 `ts` 索引 + 补写后 `ANALYZE` | 真实数据量上来后观察，必要时引入按月分区 |
| 数据量增长后的分区策略 | 本期不分区 | 数据超过千万行时评估 |
| S7 PDU 协商值探测失败时的回退 | 回退到 240 字节 | 实测遇到问题再定 |
| 日志保留天数 | 30 天 | 部署时按磁盘容量调整 |
