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
