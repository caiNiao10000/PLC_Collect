# 模型层待补的校验约束（交接给后续校验层）

> 说明：本文件是开发期交接文档，不参与编译、也不随库分发（csproj 未声明）。
> 内容的权威副本同时存在于 git 历史与实施计划文档中。

本目录下的领域模型（`DeviceConnection` / `PollGroup` / `PointConfig` / `S7Address` / `ModbusAddress` 与各枚举）
是**纯数据 record，故意不含任何校验逻辑**——这是任务书的设计意图（"只承载数据，不含行为"）。
因此下面这些约束**目前只写在 XML 注释里，代码层一个都不拦**。

本文件的作用是**明确交接点**：这些约束必须在后续的独立校验层里落地，不要以为模型层已经兜住了。
**本文件只是清单，不是实现**；现在也**不要**在模型层里加校验——那会破坏"纯数据"的定位。

## 1. 字段约束清单

| 字段 | 约束 |
|---|---|
| `DeviceConnection.ConnCode` | 仅允许 `[a-z0-9_]`，长度 ≤ 20 |
| `PollGroup.GroupCode` | 仅允许 `[a-z0-9_]`，长度 ≤ 20 |
| `PointConfig.ColumnName` | 全 ASCII、**组内唯一**、UTF-8 字节数 ≤ 63 |
| `PollGroup.PeriodMs` | 1000 ~ 600000 |
| `ModbusAddress.SlaveId` | 1 ~ 247 |
| `S7Address.BitOffset` | 0 ~ 7 |

`ConnCode` / `GroupCode` 参与生成表名（PostgreSQL 标识符），必须安全，不能靠调用方自觉。

> `ColumnName` 的 63 字节要特别当心：PostgreSQL 标识符上限是 63 **字节**而非字符，
> UTF-8 下一个中文字符占 3 字节，**21 个中文就到上限**。
> 列名生成逻辑（Task 3）与本校验层必须用**字节数**判定，用 `string.Length` 会漏判。
> 该列名会进入 DDL，串里带引号即构成注入面，必须由校验层挡住。

## 2. 随协议变化的必填性（最容易漏的一类）

`PointConfig` 的 `S7` 与 `Modbus` 两个地址字段**只会有其一非空**，取决于所属连接的 `Protocol`：

| 连接协议 | 地址字段要求 |
|---|---|
| `S7` | `S7` 必填，`Modbus` 必须为 `null` |
| `ModbusTcp` / `ModbusRtu` | `Modbus` 必填，`S7` 必须为 `null` |

| 连接协议 | 连接自身的必填项 |
|---|---|
| `S7` | `Host` 必填 |
| `ModbusTcp` | `Host` 必填 |
| `ModbusRtu` | `SerialPort` 必填（如 `COM3`）+ `Baud` 必填 |

数据类型与寄存器区的组合约束：

| 数据类型 | 附加要求 |
|---|---|
| `PointDataType.Bool` | S7：必填 `S7Address.BitOffset`（0~7）；Modbus：只允许 `Coil` / `DiscreteInput` 区 |

> ⚠️ `DeviceConnection.DefaultPortFor(ProtocolKind.ModbusRtu)` 返回 `0`，语义是**"不适用"**，
> **不是**"任意端口"（`0` 在 TCP 里表示"由系统分配"）。校验层与落库层必须把 RTU 的端口视为无意义值。

## 3. 后续建议的校验入口（尚未实现，一个消费者都还没有）

配置可能来自 Web 表单、JSON 加载或迁移前，**每条路径都必须在进入内存模型之前过一遍校验**，
否则脏配置会直达 SQL 建表与采集层（例如 `ColumnName` 里带引号 → DDL 注入 / 建表失败）。

建议的落点（等有了真实消费者再建，不要提前猜）：

1. Web 表单提交时的字段级校验；
2. JSON / 数据库加载后的整对象校验（含跨字段的条件必填，见第 2 节）；
3. 生成 DDL 之前的最后一道防线（列名字节数、唯一性、SQL 安全）。

## 4. 已实现的相关能力（供校验/热加载复用）

- `ConfigComparer`（`ConfigComparer.cs`）：**按内容**比较配置是否等价。
  配置热加载判断"未变则跳过重建"**必须**用它，不要用 record 的 `==` / `Equals`——
  `PollGroup.Points` 是 `IReadOnlyList<PointConfig>`，record 默认相等性对它做的是**引用比较**，
  内容相同也不相等，会导致每次轮询都重建全部采集组（表现为采集抖动且不报错）。
  该行为已由 `ConfigComparerTests.固化为事实_record_相等性对集合成员走引用比较` 固化。
