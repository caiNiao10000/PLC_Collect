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

    [Fact]
    public void Modbus_RTU_没有端口概念_默认端口为_0()
    {
        // 0 的语义是"不适用"（RTU 走串口），不是 TCP 语义里的"任意端口"。
        DeviceConnection.DefaultPortFor(ProtocolKind.ModbusRtu).Should().Be(0);
    }

    [Fact]
    public void 未定义的协议值应抛出_ArgumentOutOfRangeException()
    {
        Action act = () => DeviceConnection.DefaultPortFor((ProtocolKind)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
