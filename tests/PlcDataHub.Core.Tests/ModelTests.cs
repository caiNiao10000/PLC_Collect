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
    public void Modbus_地址的位偏移默认是_0_且三参数构造仍然合法()
    {
        // Task 7 给 ModbusAddress 补了寄存器内位偏移（BOOL 点用），并刻意用**带默认值的可选参数**，
        // 使"三参数构造"（规格 3.2 节的 mb_slave/mb_reg_area/mb_address 三个字段）保持合法、
        // 全部既有构造点与配置读入路径不受影响。这条用例把该性质钉住：
        // 若有人把 BitOffset 改成必填参数，这里会立刻编译失败——那正是我们要的信号。
        var threeArgs = new ModbusAddress(1, ModbusRegisterArea.HoldingRegister, 100);

        threeArgs.BitOffset.Should().Be(0, "不写位偏移时语义是'取寄存器最低位'");

        var withBit = threeArgs with { BitOffset = 9 };

        withBit.RegisterAddress.Should().Be(100);
        withBit.BitOffset.Should().Be(9);
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
