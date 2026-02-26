using cunes.Bus;
using cunes.Cpu;
using cunes.Ppu;

namespace cunes.Tests;

public static class CpuSelfTests
{
    private const byte FlagCarry = 1 << 0;
    private const byte FlagZero = 1 << 1;
    private const byte FlagInterruptDisable = 1 << 2;
    private const byte FlagDecimal = 1 << 3;
    private const byte FlagOverflow = 1 << 6;
    private const byte FlagNegative = 1 << 7;

    public static bool RunAll()
    {
        var tests = new List<(string Name, Action Body)>
        {
            ("LDA immediate sets A and Zero", TestLdaImmediate),
            ("LDX immediate sets X and Negative", TestLdxImmediate),
            ("LDY immediate sets Y and Zero", TestLdyImmediate),
            ("TAX copies A to X and Negative", TestTax),
            ("TXA copies X to A and Negative", TestTxa),
            ("TAY/TYA transfer through Y", TestTayTya),
            ("TXS/TSX transfer stack pointer", TestTxsTsx),
            ("STA zero-page writes RAM", TestStaZeroPage),
            ("STX/STY zero-page write RAM", TestStxStyZeroPage),
            ("ADC immediate adds correctly", TestAdcImmediate),
            ("ADC immediate sets overflow on signed overflow", TestAdcOverflow),
            ("SBC immediate subtracts correctly", TestSbcImmediate),
            ("AND/ORA/EOR chain works", TestAndOraEor),
            ("ASL accumulator shifts and carry", TestAslAccumulator),
            ("LSR accumulator shifts and carry", TestLsrAccumulator),
            ("ROL accumulator rotates through carry", TestRolAccumulator),
            ("ROR accumulator rotates through carry", TestRorAccumulator),
            ("INC/DEC memory roundtrip", TestIncDecMemory),
            ("INX/DEX/INY/DEY update counters", TestInxDexInyDey),
            ("CMP immediate sets C and Z", TestCmpImmediate),
            ("CPX and CPY compare correctly", TestCpxCpy),
            ("BIT updates Z/V/N flags", TestBit),
            ("PHA/PLA roundtrip accumulator", TestPhaPla),
            ("PHP/PLP restore carry", TestPhpPlp),
            ("JSR/RTS returns to caller", TestJsrRts),
            ("RTI restores PC and status", TestRti),
            ("JMP absolute jumps to target", TestJmpAbsolute),
            ("JMP indirect uses 6502 page-wrap behavior", TestJmpIndirectWrap),
            ("BEQ branches when Zero set", TestBeqTaken),
            ("BNE branches when Zero clear", TestBneTaken),
            ("BCC/BCS branch on carry", TestBccBcs),
            ("BMI/BPL branch on negative", TestBmiBpl),
            ("BVC/BVS branch on overflow", TestBvcBvs),
            ("Flag ops CLC/SEC CLD/SED CLI/SEI CLV", TestFlagInstructions),
            ("IRQ triggers only when Interrupt Disable is clear", TestIrqMasking),
            ("BRK pushes stack and sets interrupt", TestBrk),
            ("NOP leaves state unchanged", TestNop),
            ("LDA (zp,X) resolves indexed indirect", TestLdaIzx),
            ("LDA zp,X wraps inside zero-page", TestLdaZpxWrap),
            ("LDA absolute,X reads across page", TestLdaAbxPageCross),
            ("LDA (zp),Y reads across page", TestLdaIzyPageCross)
        };

        var passed = 0;

        foreach (var (name, body) in tests)
        {
            try
            {
                body();
                Console.WriteLine($"[PASS] {name}");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] {name}: {ex.Message}");
            }
        }

        Console.WriteLine($"CPU self-tests: {passed}/{tests.Count} passed.");
        return passed == tests.Count;
    }

    private static void TestLdaImmediate()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA9, 0x00 });
        RunCpuCycles(cpu, 8 + 2);

        AssertEquals((byte)0x00, cpu.A, "A");
        AssertTrue(HasFlag(cpu.Status, FlagZero), "Zero flag should be set");
        AssertTrue(!HasFlag(cpu.Status, FlagNegative), "Negative flag should be clear");
    }

    private static void TestLdxImmediate()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA2, 0xFF });
        RunCpuCycles(cpu, 8 + 2);

        AssertEquals((byte)0xFF, cpu.X, "X");
        AssertTrue(HasFlag(cpu.Status, FlagNegative), "Negative flag should be set");
    }

    private static void TestLdyImmediate()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA0, 0x00 });
        RunCpuCycles(cpu, 8 + 2);

        AssertEquals((byte)0x00, cpu.Y, "Y");
        AssertTrue(HasFlag(cpu.Status, FlagZero), "Zero flag should be set");
    }

    private static void TestTax()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA9, 0x80, 0xAA });
        RunCpuCycles(cpu, 8 + 2 + 2);

        AssertEquals((byte)0x80, cpu.X, "X");
        AssertTrue(HasFlag(cpu.Status, FlagNegative), "Negative flag should be set");
    }

    private static void TestTxa()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA2, 0x80, 0x8A });
        RunCpuCycles(cpu, 8 + 2 + 2);

        AssertEquals((byte)0x80, cpu.A, "A");
        AssertTrue(HasFlag(cpu.Status, FlagNegative), "Negative flag should be set");
    }

    private static void TestTayTya()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA9, 0x7F, 0xA8, 0xA9, 0x00, 0x98 });
        RunCpuCycles(cpu, 8 + 2 + 2 + 2 + 2);

        AssertEquals((byte)0x7F, cpu.A, "A");
        AssertEquals((byte)0x7F, cpu.Y, "Y");
    }

    private static void TestTxsTsx()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA2, 0x10, 0x9A, 0xBA });
        RunCpuCycles(cpu, 8 + 2 + 2 + 2);

        AssertEquals((byte)0x10, cpu.StackPointer, "SP");
        AssertEquals((byte)0x10, cpu.X, "X");
    }

    private static void TestStaZeroPage()
    {
        var (cpu, bus, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA9, 0x42, 0x85, 0x10 });
        RunCpuCycles(cpu, 8 + 2 + 3);

        AssertEquals((byte)0x42, bus.Read(0x0010), "RAM[$0010]");
    }

    private static void TestStxStyZeroPage()
    {
        var (cpu, bus, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA2, 0x12, 0xA0, 0x34, 0x86, 0x20, 0x84, 0x21 });
        RunCpuCycles(cpu, 8 + 2 + 2 + 3 + 3);

        AssertEquals((byte)0x12, bus.Read(0x0020), "RAM[$0020]");
        AssertEquals((byte)0x34, bus.Read(0x0021), "RAM[$0021]");
    }

    private static void TestAdcImmediate()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA9, 0x01, 0x69, 0x01 });
        RunCpuCycles(cpu, 8 + 2 + 2);

        AssertEquals((byte)0x02, cpu.A, "A");
        AssertTrue(!HasFlag(cpu.Status, FlagCarry), "Carry flag should be clear");
        AssertTrue(!HasFlag(cpu.Status, FlagZero), "Zero flag should be clear");
    }

    private static void TestAdcOverflow()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA9, 0x50, 0x69, 0x50 });
        RunCpuCycles(cpu, 8 + 2 + 2);

        AssertEquals((byte)0xA0, cpu.A, "A");
        AssertTrue(HasFlag(cpu.Status, FlagOverflow), "Overflow flag should be set");
        AssertTrue(HasFlag(cpu.Status, FlagNegative), "Negative flag should be set");
        AssertTrue(!HasFlag(cpu.Status, FlagCarry), "Carry flag should be clear");
    }

    private static void TestSbcImmediate()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0x38, 0xA9, 0x05, 0xE9, 0x03 });
        RunCpuCycles(cpu, 8 + 2 + 2 + 2);

        AssertEquals((byte)0x02, cpu.A, "A");
        AssertTrue(HasFlag(cpu.Status, FlagCarry), "Carry flag should be set");
        AssertTrue(!HasFlag(cpu.Status, FlagZero), "Zero flag should be clear");
    }

    private static void TestAndOraEor()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA9, 0xF0, 0x29, 0x0F, 0x09, 0x80, 0x49, 0xFF });
        RunCpuCycles(cpu, 8 + 2 + 2 + 2 + 2);

        AssertEquals((byte)0x7F, cpu.A, "A");
        AssertTrue(!HasFlag(cpu.Status, FlagZero), "Zero flag should be clear");
        AssertTrue(!HasFlag(cpu.Status, FlagNegative), "Negative flag should be clear");
    }

    private static void TestAslAccumulator()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA9, 0x81, 0x0A });
        RunCpuCycles(cpu, 8 + 2 + 2);

        AssertEquals((byte)0x02, cpu.A, "A");
        AssertTrue(HasFlag(cpu.Status, FlagCarry), "Carry should be set");
    }

    private static void TestLsrAccumulator()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA9, 0x01, 0x4A });
        RunCpuCycles(cpu, 8 + 2 + 2);

        AssertEquals((byte)0x00, cpu.A, "A");
        AssertTrue(HasFlag(cpu.Status, FlagCarry), "Carry should be set");
        AssertTrue(HasFlag(cpu.Status, FlagZero), "Zero should be set");
    }

    private static void TestRolAccumulator()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0x18, 0xA9, 0x80, 0x2A });
        RunCpuCycles(cpu, 8 + 2 + 2 + 2);

        AssertEquals((byte)0x00, cpu.A, "A");
        AssertTrue(HasFlag(cpu.Status, FlagCarry), "Carry should be set");
        AssertTrue(HasFlag(cpu.Status, FlagZero), "Zero should be set");
    }

    private static void TestRorAccumulator()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0x38, 0xA9, 0x00, 0x6A });
        RunCpuCycles(cpu, 8 + 2 + 2 + 2);

        AssertEquals((byte)0x80, cpu.A, "A");
        AssertTrue(!HasFlag(cpu.Status, FlagCarry), "Carry should be clear");
        AssertTrue(HasFlag(cpu.Status, FlagNegative), "Negative should be set");
    }

    private static void TestIncDecMemory()
    {
        var (cpu, bus, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA9, 0x10, 0x85, 0x40, 0xE6, 0x40, 0xC6, 0x40 });
        RunCpuCycles(cpu, 8 + 2 + 3 + 5 + 5);

        AssertEquals((byte)0x10, bus.Read(0x0040), "RAM[$0040]");
    }

    private static void TestInxDexInyDey()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xE8, 0xCA, 0xC8, 0x88 });
        RunCpuCycles(cpu, 8 + 2 + 2 + 2 + 2);

        AssertEquals((byte)0x00, cpu.X, "X");
        AssertEquals((byte)0x00, cpu.Y, "Y");
        AssertTrue(HasFlag(cpu.Status, FlagZero), "Zero should be set");
    }

    private static void TestCmpImmediate()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA9, 0x10, 0xC9, 0x10 });
        RunCpuCycles(cpu, 8 + 2 + 2);

        AssertTrue(HasFlag(cpu.Status, FlagCarry), "Carry flag should be set");
        AssertTrue(HasFlag(cpu.Status, FlagZero), "Zero flag should be set");
        AssertTrue(!HasFlag(cpu.Status, FlagNegative), "Negative flag should be clear");
    }

    private static void TestCpxCpy()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA2, 0x05, 0xE0, 0x03, 0xA0, 0x02, 0xC0, 0x02 });
        RunCpuCycles(cpu, 8 + 2 + 2 + 2 + 2);

        AssertTrue(HasFlag(cpu.Status, FlagCarry), "Carry should be set");
        AssertTrue(HasFlag(cpu.Status, FlagZero), "Zero should be set");
    }

    private static void TestBit()
    {
        var (cpu, bus, _) = CreateCpu();
        bus.Write(0x0030, 0xC0);
        LoadProgramAndReset(cpu, new byte[] { 0xA9, 0x40, 0x24, 0x30 });
        RunCpuCycles(cpu, 8 + 2 + 3);

        AssertTrue(!HasFlag(cpu.Status, FlagZero), "Zero should be clear");
        AssertTrue(HasFlag(cpu.Status, FlagOverflow), "Overflow should be set");
        AssertTrue(HasFlag(cpu.Status, FlagNegative), "Negative should be set");
    }

    private static void TestPhaPla()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA9, 0x3C, 0x48, 0xA9, 0x00, 0x68 });
        RunCpuCycles(cpu, 8 + 2 + 3 + 2 + 4);

        AssertEquals((byte)0x3C, cpu.A, "A");
        AssertTrue(!HasFlag(cpu.Status, FlagZero), "Zero flag should be clear after PLA");
        AssertTrue(!HasFlag(cpu.Status, FlagNegative), "Negative flag should be clear after PLA");
    }

    private static void TestPhpPlp()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0x38, 0x08, 0x18, 0x28 });
        RunCpuCycles(cpu, 8 + 2 + 3 + 2 + 4);

        AssertTrue(HasFlag(cpu.Status, FlagCarry), "Carry should be restored by PLP");
    }

    private static void TestJsrRts()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[]
        {
            0x20, 0x06, 0x00,
            0xA9, 0x01,
            0x00,
            0xA9, 0x07,
            0x60
        });

        RunCpuCycles(cpu, 8 + 6 + 2 + 6 + 2);
        AssertEquals((byte)0x01, cpu.A, "A");
    }

    private static void TestRti()
    {
        var (cpu, _, _) = CreateCpu();

        cpu.Write(0x0000, 0x40); // RTI
        cpu.Write(0x0005, 0xA9); // LDA #$66
        cpu.Write(0x0006, 0x66);

        cpu.Write(0x01FE, FlagCarry);
        cpu.Write(0x01FF, 0x05);
        cpu.Write(0x0100, 0x00);

        cpu.Reset();
        RunCpuCycles(cpu, 8 + 6 + 2);

        AssertEquals((byte)0x66, cpu.A, "A");
        AssertTrue(HasFlag(cpu.Status, FlagCarry), "Carry should be restored from stack");
    }

    private static void TestJmpAbsolute()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0x4C, 0x05, 0x00, 0xA9, 0x01, 0xA9, 0x07 });
        RunCpuCycles(cpu, 8 + 3 + 2);

        AssertEquals((byte)0x07, cpu.A, "A");
    }

    private static void TestJmpIndirectWrap()
    {
        var (cpu, bus, _) = CreateCpu();
        bus.Write(0x02FF, 0x06);
        bus.Write(0x0200, 0x00);

        LoadProgramAndReset(cpu, new byte[] { 0x6C, 0xFF, 0x02, 0xEA, 0xEA, 0xEA, 0xA9, 0x44 });
        RunCpuCycles(cpu, 8 + 5 + 2);

        AssertEquals((byte)0x44, cpu.A, "A");
    }

    private static void TestBeqTaken()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[]
        {
            0xA9, 0x00,
            0xF0, 0x02,
            0xA9, 0x01,
            0xA9, 0x05
        });

        RunCpuCycles(cpu, 8 + 2 + 3 + 2);
        AssertEquals((byte)0x05, cpu.A, "A");
    }

    private static void TestBneTaken()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[]
        {
            0xA9, 0x01,
            0xD0, 0x02,
            0xA9, 0x03,
            0xA9, 0x07
        });

        RunCpuCycles(cpu, 8 + 2 + 3 + 2);
        AssertEquals((byte)0x07, cpu.A, "A");
    }

    private static void TestBccBcs()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[]
        {
            0x18,
            0x90, 0x02,
            0xA9, 0x01,
            0xA9, 0x02,
            0x38,
            0xB0, 0x02,
            0xA9, 0x03,
            0xA9, 0x04
        });

        RunCpuCycles(cpu, 8 + 2 + 3 + 2 + 2 + 3 + 2);
        AssertEquals((byte)0x04, cpu.A, "A");
    }

    private static void TestBmiBpl()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[]
        {
            0xA9, 0x80,
            0x30, 0x02,
            0xA9, 0x01,
            0xA9, 0x02,
            0xA9, 0x01,
            0x10, 0x02,
            0xA9, 0x03,
            0xA9, 0x04
        });

        RunCpuCycles(cpu, 8 + 2 + 3 + 2 + 2 + 3 + 2);
        AssertEquals((byte)0x04, cpu.A, "A");
    }

    private static void TestBvcBvs()
    {
        var (cpu, bus, _) = CreateCpu();
        bus.Write(0x0020, 0x40);

        LoadProgramAndReset(cpu, new byte[]
        {
            0xA9, 0x00,
            0x24, 0x20,
            0x70, 0x02,
            0xA9, 0x01,
            0xA9, 0x02,
            0xB8,
            0x50, 0x02,
            0xA9, 0x03,
            0xA9, 0x04
        });

        RunCpuCycles(cpu, 8 + 2 + 3 + 3 + 2 + 2 + 3 + 2);
        AssertEquals((byte)0x04, cpu.A, "A");
    }

    private static void TestFlagInstructions()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0x38, 0x18, 0xF8, 0xD8, 0x78, 0x58, 0xB8 });
        RunCpuCycles(cpu, 8 + 2 + 2 + 2 + 2 + 2 + 2 + 2);

        AssertTrue(!HasFlag(cpu.Status, FlagCarry), "Carry should be clear");
        AssertTrue(!HasFlag(cpu.Status, FlagDecimal), "Decimal should be clear");
        AssertTrue(!HasFlag(cpu.Status, FlagInterruptDisable), "Interrupt disable should be clear");
        AssertTrue(!HasFlag(cpu.Status, FlagOverflow), "Overflow should be clear");
    }

    private static void TestIrqMasking()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0x58, 0xEA, 0xEA }); // CLI; NOP; NOP

        // While I=1 after reset, IRQ should be pending but not taken.
        cpu.Irq();
        RunCpuCycles(cpu, 8);
        AssertEquals((ushort)0x0000, cpu.ProgramCounter, "PC before CLI");

        // CLI executes.
        RunCpuCycles(cpu, 2);
        AssertEquals((ushort)0x0001, cpu.ProgramCounter, "PC after CLI");

        // On 6502, IRQ recognition is inhibited for one following instruction.
        RunCpuCycles(cpu, 2); // NOP
        AssertEquals((ushort)0x0002, cpu.ProgramCounter, "PC after one instruction post-CLI");

        RunCpuCycles(cpu, 7);
        AssertEquals((byte)0xFA, cpu.StackPointer, "SP after IRQ push");
        AssertTrue(HasFlag(cpu.Status, FlagInterruptDisable), "Interrupt disable should be set by IRQ");
    }

    private static void TestBrk()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0x00 });
        RunCpuCycles(cpu, 8 + 7);

        AssertEquals((byte)0xFA, cpu.StackPointer, "SP");
        AssertTrue(HasFlag(cpu.Status, FlagInterruptDisable), "Interrupt disable should be set");
    }

    private static void TestNop()
    {
        var (cpu, _, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xEA, 0xA9, 0x55 });
        RunCpuCycles(cpu, 8 + 2 + 2);

        AssertEquals((byte)0x55, cpu.A, "A");
        AssertEquals((ushort)0x0003, cpu.ProgramCounter, "PC");
    }

    private static void TestLdaIzx()
    {
        var (cpu, bus, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA2, 0x04, 0xA1, 0x10 });

        bus.Write(0x0014, 0x40);
        bus.Write(0x0015, 0x00);
        bus.Write(0x0040, 0x9B);

        RunCpuCycles(cpu, 8 + 2 + 6);
        AssertEquals((byte)0x9B, cpu.A, "A");
    }

    private static void TestLdaZpxWrap()
    {
        var (cpu, bus, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA2, 0x90, 0xB5, 0x80 });
        bus.Write(0x0010, 0x5C);

        RunCpuCycles(cpu, 8 + 2 + 4);
        AssertEquals((byte)0x5C, cpu.A, "A");
    }

    private static void TestLdaAbxPageCross()
    {
        var (cpu, bus, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA2, 0x01, 0xBD, 0xFF, 0x00 });
        bus.Write(0x0100, 0x77);

        RunCpuCycles(cpu, 8 + 2 + 5);
        AssertEquals((byte)0x77, cpu.A, "A");
    }

    private static void TestLdaIzyPageCross()
    {
        var (cpu, bus, _) = CreateCpu();
        LoadProgramAndReset(cpu, new byte[] { 0xA0, 0x01, 0xB1, 0x10 });

        bus.Write(0x0010, 0xFF);
        bus.Write(0x0011, 0x00);
        bus.Write(0x0100, 0x5A);

        RunCpuCycles(cpu, 8 + 2 + 6);
        AssertEquals((byte)0x5A, cpu.A, "A");
    }

    private static (Cpu6502 Cpu, SystemBus Bus, Ppu2C02 Ppu) CreateCpu()
    {
        var cpu = new Cpu6502();
        var ppu = new Ppu2C02();
        var bus = new SystemBus(cpu, ppu);
        return (cpu, bus, ppu);
    }

    private static void LoadProgramAndReset(Cpu6502 cpu, byte[] program)
    {
        for (ushort i = 0; i < program.Length; i++)
        {
            cpu.Write(i, program[i]);
        }

        cpu.Reset();
    }

    private static void RunCpuCycles(Cpu6502 cpu, int cycles)
    {
        for (var i = 0; i < cycles; i++)
        {
            cpu.Clock();
        }
    }

    private static bool HasFlag(byte status, byte flag)
    {
        return (status & flag) != 0;
    }

    private static void AssertEquals(byte expected, byte actual, string label)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException($"{label}: expected ${expected:X2}, got ${actual:X2}");
        }
    }

    private static void AssertEquals(ushort expected, ushort actual, string label)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException($"{label}: expected ${expected:X4}, got ${actual:X4}");
        }
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
