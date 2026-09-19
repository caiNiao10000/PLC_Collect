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
