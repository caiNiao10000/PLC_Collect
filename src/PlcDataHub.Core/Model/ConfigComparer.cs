using System.Linq;

namespace PlcDataHub.Core.Model;

/// <summary>
/// 配置的结构性比较。
///
/// 为什么需要它：<see cref="PollGroup"/> 与 <see cref="PointConfig"/> 是 record，
/// 但 record 的相等性对集合类型成员（<see cref="PollGroup.Points"/> 是
/// <c>IReadOnlyList&lt;PointConfig&gt;</c>）走的是<b>引用比较</b> ——
/// 内容完全相同但实例不同的两个 PollGroup <b>不相等</b>。
/// 本行为已由 ConfigComparerTests 用测试固化。
///
/// 后果：规格 5.5 节的配置热加载需要"与当前运行态比对、未变则跳过重建"，
/// 若误用 <c>==</c> 或 record 默认相等性，会永远判定为"已变更"，
/// 于是每次配置轮询都重建全部采集组，表现为采集周期性抖动且不报错。
///
/// 因此任何"配置是否变化"的判断一律使用本类的方法。
/// </summary>
public static class ConfigComparer
{
    /// <summary>按内容比较两个采集点是否等价。</summary>
    public static bool PointEquivalent(PointConfig left, PointConfig right) =>
        left.PointId == right.PointId
        && left.PointCode == right.PointCode
        && left.PointName == right.PointName
        && left.ColumnName == right.ColumnName
        && left.DataType == right.DataType
        && left.ByteOrder == right.ByteOrder
        && left.Scale.Equals(right.Scale)
        && left.Offset.Equals(right.Offset)
        && left.Enabled == right.Enabled
        && S7Equivalent(left.S7, right.S7)
        && ModbusEquivalent(left.Modbus, right.Modbus);

    /// <summary>按内容比较两个采集组是否等价（含组内全部采集点，按顺序）。</summary>
    public static bool GroupsEquivalent(PollGroup left, PollGroup right) =>
        left.GroupId == right.GroupId
        && left.ConnId == right.ConnId
        && left.GroupCode == right.GroupCode
        && left.GroupName == right.GroupName
        && left.PeriodMs == right.PeriodMs
        && left.TableName == right.TableName
        && left.Enabled == right.Enabled
        && PointsEquivalent(left.Points, right.Points);

    /// <summary>按内容+顺序比较两组采集点。</summary>
    public static bool PointsEquivalent(
        IReadOnlyList<PointConfig> left,
        IReadOnlyList<PointConfig> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!PointEquivalent(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool S7Equivalent(S7Address? left, S7Address? right) =>
        ReferenceEquals(left, right)
        || (left is not null && right is not null
            && left.Area == right.Area
            && left.Db == right.Db
            && left.ByteOffset == right.ByteOffset
            && left.BitOffset == right.BitOffset);

    private static bool ModbusEquivalent(ModbusAddress? left, ModbusAddress? right) =>
        ReferenceEquals(left, right)
        || (left is not null && right is not null
            && left.SlaveId == right.SlaveId
            && left.Area == right.Area
            && left.RegisterAddress == right.RegisterAddress);
}
