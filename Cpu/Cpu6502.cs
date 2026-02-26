using cunes.Bus;

namespace cunes.Cpu;

public sealed class Cpu6502
{
    private enum StatusFlag : byte
    {
        Carry = 1 << 0,
        Zero = 1 << 1,
        InterruptDisable = 1 << 2,
        Decimal = 1 << 3,
        Break = 1 << 4,
        Unused = 1 << 5,
        Overflow = 1 << 6,
        Negative = 1 << 7
    }

    private enum AddressModeKind
    {
        Imp,
        Acc,
        Imm,
        Zp0,
        Zpx,
        Zpy,
        Rel,
        Abs,
        Abx,
        Aby,
        Jsr,
        Ind,
        Izx,
        Izy
    }

    private delegate byte AddressModeHandler();
    private delegate byte OperationHandler();

    private sealed record Instruction(
        string Name,
        OperationHandler Operate,
        AddressModeHandler AddressMode,
        AddressModeKind AddressModeKind,
        byte Cycles);

    private readonly Instruction[] _lookup = new Instruction[256];
    private SystemBus? _bus;

    private byte _fetched;
    private ushort _addressAbsolute;
    private ushort _addressAbsoluteBase;
    private ushort _addressRelative;
    private byte _opcode;
    private byte _cyclesRemaining;
    private bool _nmiPending;
    private bool _irqPending;
    private bool _irqPendingLatched;
    private bool _irqPolledThisInstruction;
    private bool _shMicroActive;
    private byte _shMicroOpcode;
    private int _shMicroStep;
    private byte _shZeroPagePointer;
    private ushort _shBaseAddress;
    private ushort _shEffectiveAddress;
    private byte _shRawRegisterValue;
    private byte _shHighMask;
    private byte _shWriteValue;
    private bool _shRdyLowTwoCyclesBeforeWrite;
    private Instruction _currentInstruction;

    public byte A { get; private set; }
    public byte X { get; private set; }
    public byte Y { get; private set; }
    public byte StackPointer { get; private set; }
    public ushort ProgramCounter { get; private set; }
    public byte Status { get; private set; }
    public ulong Cycles { get; private set; }

    public Cpu6502()
    {
        _currentInstruction = new Instruction("???", XXX, IMP, AddressModeKind.Imp, 2);
        BuildInstructionTable();
    }

    public void ConnectBus(SystemBus bus)
    {
        _bus = bus;
    }

    public void Reset()
    {
        A = 0x00;
        X = 0x00;
        Y = 0x00;

        StackPointer = 0xFD;
        Status = 0x00;
        SetFlag(StatusFlag.Unused, true);
        SetFlag(StatusFlag.InterruptDisable, true);

        var low = Read(0xFFFC);
        var high = Read(0xFFFD);
        ProgramCounter = (ushort)((high << 8) | low);

        _addressAbsolute = 0x0000;
        _addressAbsoluteBase = 0x0000;
        _addressRelative = 0x0000;
        _fetched = 0x00;

        _cyclesRemaining = 8;
        _nmiPending = false;
        _irqPending = false;
        _irqPendingLatched = false;
        _irqPolledThisInstruction = false;
        _shMicroActive = false;
        _shMicroOpcode = 0;
        _shMicroStep = 0;
        _shZeroPagePointer = 0;
        _shBaseAddress = 0;
        _shEffectiveAddress = 0;
        _shRawRegisterValue = 0;
        _shHighMask = 0;
        _shWriteValue = 0;
        _shRdyLowTwoCyclesBeforeWrite = false;
        Cycles = 0;
    }

    public void Clock()
    {
        if (_shMicroActive)
        {
            RunShMicroStep();
            Cycles++;
            return;
        }

        if (_cyclesRemaining == 0)
        {
            if (_nmiPending)
            {
                HandleNmi();
                _nmiPending = false;
                _cyclesRemaining = 8;
                _cyclesRemaining--;
                Cycles++;
                return;
            }

            if (_irqPendingLatched)
            {
                HandleIrq();
                _irqPendingLatched = false;
                _irqPending = false;
                _cyclesRemaining = 7;
                _cyclesRemaining--;
                Cycles++;
                return;
            }

            _opcode = Read(ProgramCounter++);
            _currentInstruction = _lookup[_opcode];
            _irqPolledThisInstruction = false;

            if (IsShMicroOpcode(_opcode))
            {
                StartShMicrocode(_opcode);
                Cycles++;
                return;
            }

            _cyclesRemaining = _currentInstruction.Cycles;

            var additionalCycleFromAddressMode = _currentInstruction.AddressMode();
            var additionalCycleFromOperation = _currentInstruction.Operate();

            if (!_irqPolledThisInstruction && _opcode != 0x00)
            {
                PollIrq();
            }

            _cyclesRemaining += (byte)(additionalCycleFromAddressMode & additionalCycleFromOperation);
            SetFlag(StatusFlag.Unused, true);
        }

        _cyclesRemaining--;
        Cycles++;
    }

    public void HaltCycle()
    {
        if (_shMicroActive)
        {
            if (IsShTwoCyclesBeforeWriteStep())
            {
                _shRdyLowTwoCyclesBeforeWrite = true;
            }

            // RDY does not hold writes; commit the SH* write cycle if we are on it.
            if (IsShWriteStep())
            {
                RunShMicroStep();
            }
        }

        Cycles++;
    }

    public byte Read(ushort address)
    {
        return _bus?.Read(address) ?? (byte)0x00;
    }

    public void Write(ushort address, byte data)
    {
        _bus?.Write(address, data);
    }

    public void SetProgramCounter(ushort address)
    {
        ProgramCounter = address;
    }

    public void Nmi()
    {
        _nmiPending = true;
    }

    public void Irq()
    {
        _irqPending = true;
    }

    private void PollIrq()
    {
        _irqPolledThisInstruction = true;
        if (_irqPending && !GetFlag(StatusFlag.InterruptDisable))
        {
            _irqPendingLatched = true;
        }
    }

    private void BuildInstructionTable()
    {
        for (var i = 0; i < _lookup.Length; i++)
        {
            _lookup[i] = new Instruction("???", XXX, IMP, AddressModeKind.Imp, 2);
        }

        SetInstruction(0x00, "BRK", BRK, IMP, AddressModeKind.Imp, 7);
        SetInstruction(0xEA, "NOP", NOP, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0x1A, "NOP", NOP, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0x3A, "NOP", NOP, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0x5A, "NOP", NOP, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0x7A, "NOP", NOP, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0xDA, "NOP", NOP, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0xFA, "NOP", NOP, IMP, AddressModeKind.Imp, 2);

        SetInstruction(0x80, "NOP", NOP, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0x82, "NOP", NOP, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0x89, "NOP", NOP, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0xC2, "NOP", NOP, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0xE2, "NOP", NOP, IMM, AddressModeKind.Imm, 2);

        SetInstruction(0x04, "NOP", NOP, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0x44, "NOP", NOP, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0x64, "NOP", NOP, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0x14, "NOP", NOP, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0x34, "NOP", NOP, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0x54, "NOP", NOP, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0x74, "NOP", NOP, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0xD4, "NOP", NOP, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0xF4, "NOP", NOP, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0x0C, "NOP", NOP, ABS, AddressModeKind.Abs, 4);
        SetInstruction(0x1C, "NOP", NOP, ABX, AddressModeKind.Abx, 4);
        SetInstruction(0x3C, "NOP", NOP, ABX, AddressModeKind.Abx, 4);
        SetInstruction(0x5C, "NOP", NOP, ABX, AddressModeKind.Abx, 4);
        SetInstruction(0x7C, "NOP", NOP, ABX, AddressModeKind.Abx, 4);
        SetInstruction(0xDC, "NOP", NOP, ABX, AddressModeKind.Abx, 4);
        SetInstruction(0xFC, "NOP", NOP, ABX, AddressModeKind.Abx, 4);

        SetInstruction(0xA9, "LDA", LDA, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0xA5, "LDA", LDA, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0xB5, "LDA", LDA, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0xAD, "LDA", LDA, ABS, AddressModeKind.Abs, 4);
        SetInstruction(0xBD, "LDA", LDA, ABX, AddressModeKind.Abx, 4);
        SetInstruction(0xB9, "LDA", LDA, ABY, AddressModeKind.Aby, 4);
        SetInstruction(0xA1, "LDA", LDA, IZX, AddressModeKind.Izx, 6);
        SetInstruction(0xB1, "LDA", LDA, IZY, AddressModeKind.Izy, 5);

        SetInstruction(0xA2, "LDX", LDX, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0xA6, "LDX", LDX, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0xB6, "LDX", LDX, ZPY, AddressModeKind.Zpy, 4);
        SetInstruction(0xAE, "LDX", LDX, ABS, AddressModeKind.Abs, 4);
        SetInstruction(0xBE, "LDX", LDX, ABY, AddressModeKind.Aby, 4);

        SetInstruction(0xA0, "LDY", LDY, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0xA4, "LDY", LDY, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0xB4, "LDY", LDY, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0xAC, "LDY", LDY, ABS, AddressModeKind.Abs, 4);
        SetInstruction(0xBC, "LDY", LDY, ABX, AddressModeKind.Abx, 4);

        SetInstruction(0x85, "STA", STA, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0x95, "STA", STA, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0x8D, "STA", STA, ABS, AddressModeKind.Abs, 4);
        SetInstruction(0x9D, "STA", STA, ABX, AddressModeKind.Abx, 5);
        SetInstruction(0x99, "STA", STA, ABY, AddressModeKind.Aby, 5);
        SetInstruction(0x81, "STA", STA, IZX, AddressModeKind.Izx, 6);
        SetInstruction(0x91, "STA", STA, IZY, AddressModeKind.Izy, 6);

        SetInstruction(0x86, "STX", STX, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0x96, "STX", STX, ZPY, AddressModeKind.Zpy, 4);
        SetInstruction(0x8E, "STX", STX, ABS, AddressModeKind.Abs, 4);

        SetInstruction(0x84, "STY", STY, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0x94, "STY", STY, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0x8C, "STY", STY, ABS, AddressModeKind.Abs, 4);

        SetInstruction(0xAA, "TAX", TAX, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0xA8, "TAY", TAY, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0x8A, "TXA", TXA, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0x98, "TYA", TYA, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0xBA, "TSX", TSX, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0x9A, "TXS", TXS, IMP, AddressModeKind.Imp, 2);

        SetInstruction(0x48, "PHA", PHA, IMP, AddressModeKind.Imp, 3);
        SetInstruction(0x68, "PLA", PLA, IMP, AddressModeKind.Imp, 4);
        SetInstruction(0x08, "PHP", PHP, IMP, AddressModeKind.Imp, 3);
        SetInstruction(0x28, "PLP", PLP, IMP, AddressModeKind.Imp, 4);

        SetInstruction(0x69, "ADC", ADC, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0x65, "ADC", ADC, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0x75, "ADC", ADC, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0x6D, "ADC", ADC, ABS, AddressModeKind.Abs, 4);
        SetInstruction(0x7D, "ADC", ADC, ABX, AddressModeKind.Abx, 4);
        SetInstruction(0x79, "ADC", ADC, ABY, AddressModeKind.Aby, 4);
        SetInstruction(0x61, "ADC", ADC, IZX, AddressModeKind.Izx, 6);
        SetInstruction(0x71, "ADC", ADC, IZY, AddressModeKind.Izy, 5);

        SetInstruction(0xE9, "SBC", SBC, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0xE5, "SBC", SBC, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0xF5, "SBC", SBC, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0xED, "SBC", SBC, ABS, AddressModeKind.Abs, 4);
        SetInstruction(0xFD, "SBC", SBC, ABX, AddressModeKind.Abx, 4);
        SetInstruction(0xF9, "SBC", SBC, ABY, AddressModeKind.Aby, 4);
        SetInstruction(0xE1, "SBC", SBC, IZX, AddressModeKind.Izx, 6);
        SetInstruction(0xF1, "SBC", SBC, IZY, AddressModeKind.Izy, 5);

        SetInstruction(0x29, "AND", AND, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0x25, "AND", AND, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0x35, "AND", AND, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0x2D, "AND", AND, ABS, AddressModeKind.Abs, 4);
        SetInstruction(0x3D, "AND", AND, ABX, AddressModeKind.Abx, 4);
        SetInstruction(0x39, "AND", AND, ABY, AddressModeKind.Aby, 4);
        SetInstruction(0x21, "AND", AND, IZX, AddressModeKind.Izx, 6);
        SetInstruction(0x31, "AND", AND, IZY, AddressModeKind.Izy, 5);

        SetInstruction(0x09, "ORA", ORA, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0x05, "ORA", ORA, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0x15, "ORA", ORA, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0x0D, "ORA", ORA, ABS, AddressModeKind.Abs, 4);
        SetInstruction(0x1D, "ORA", ORA, ABX, AddressModeKind.Abx, 4);
        SetInstruction(0x19, "ORA", ORA, ABY, AddressModeKind.Aby, 4);
        SetInstruction(0x01, "ORA", ORA, IZX, AddressModeKind.Izx, 6);
        SetInstruction(0x11, "ORA", ORA, IZY, AddressModeKind.Izy, 5);

        SetInstruction(0x49, "EOR", EOR, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0x45, "EOR", EOR, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0x55, "EOR", EOR, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0x4D, "EOR", EOR, ABS, AddressModeKind.Abs, 4);
        SetInstruction(0x5D, "EOR", EOR, ABX, AddressModeKind.Abx, 4);
        SetInstruction(0x59, "EOR", EOR, ABY, AddressModeKind.Aby, 4);
        SetInstruction(0x41, "EOR", EOR, IZX, AddressModeKind.Izx, 6);
        SetInstruction(0x51, "EOR", EOR, IZY, AddressModeKind.Izy, 5);

        SetInstruction(0x0A, "ASL", ASL, ACC, AddressModeKind.Acc, 2);
        SetInstruction(0x06, "ASL", ASL, ZP0, AddressModeKind.Zp0, 5);
        SetInstruction(0x16, "ASL", ASL, ZPX, AddressModeKind.Zpx, 6);
        SetInstruction(0x0E, "ASL", ASL, ABS, AddressModeKind.Abs, 6);
        SetInstruction(0x1E, "ASL", ASL, ABX, AddressModeKind.Abx, 7);

        SetInstruction(0x4A, "LSR", LSR, ACC, AddressModeKind.Acc, 2);
        SetInstruction(0x46, "LSR", LSR, ZP0, AddressModeKind.Zp0, 5);
        SetInstruction(0x56, "LSR", LSR, ZPX, AddressModeKind.Zpx, 6);
        SetInstruction(0x4E, "LSR", LSR, ABS, AddressModeKind.Abs, 6);
        SetInstruction(0x5E, "LSR", LSR, ABX, AddressModeKind.Abx, 7);

        SetInstruction(0x2A, "ROL", ROL, ACC, AddressModeKind.Acc, 2);
        SetInstruction(0x26, "ROL", ROL, ZP0, AddressModeKind.Zp0, 5);
        SetInstruction(0x36, "ROL", ROL, ZPX, AddressModeKind.Zpx, 6);
        SetInstruction(0x2E, "ROL", ROL, ABS, AddressModeKind.Abs, 6);
        SetInstruction(0x3E, "ROL", ROL, ABX, AddressModeKind.Abx, 7);

        SetInstruction(0x6A, "ROR", ROR, ACC, AddressModeKind.Acc, 2);
        SetInstruction(0x66, "ROR", ROR, ZP0, AddressModeKind.Zp0, 5);
        SetInstruction(0x76, "ROR", ROR, ZPX, AddressModeKind.Zpx, 6);
        SetInstruction(0x6E, "ROR", ROR, ABS, AddressModeKind.Abs, 6);
        SetInstruction(0x7E, "ROR", ROR, ABX, AddressModeKind.Abx, 7);

        SetInstruction(0xE6, "INC", INC, ZP0, AddressModeKind.Zp0, 5);
        SetInstruction(0xF6, "INC", INC, ZPX, AddressModeKind.Zpx, 6);
        SetInstruction(0xEE, "INC", INC, ABS, AddressModeKind.Abs, 6);
        SetInstruction(0xFE, "INC", INC, ABX, AddressModeKind.Abx, 7);

        SetInstruction(0xC6, "DEC", DEC, ZP0, AddressModeKind.Zp0, 5);
        SetInstruction(0xD6, "DEC", DEC, ZPX, AddressModeKind.Zpx, 6);
        SetInstruction(0xCE, "DEC", DEC, ABS, AddressModeKind.Abs, 6);
        SetInstruction(0xDE, "DEC", DEC, ABX, AddressModeKind.Abx, 7);

        SetInstruction(0xE8, "INX", INX, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0xCA, "DEX", DEX, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0xC8, "INY", INY, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0x88, "DEY", DEY, IMP, AddressModeKind.Imp, 2);

        SetInstruction(0xC9, "CMP", CMP, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0xC5, "CMP", CMP, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0xD5, "CMP", CMP, ZPX, AddressModeKind.Zpx, 4);
        SetInstruction(0xCD, "CMP", CMP, ABS, AddressModeKind.Abs, 4);
        SetInstruction(0xDD, "CMP", CMP, ABX, AddressModeKind.Abx, 4);
        SetInstruction(0xD9, "CMP", CMP, ABY, AddressModeKind.Aby, 4);
        SetInstruction(0xC1, "CMP", CMP, IZX, AddressModeKind.Izx, 6);
        SetInstruction(0xD1, "CMP", CMP, IZY, AddressModeKind.Izy, 5);

        SetInstruction(0xE0, "CPX", CPX, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0xE4, "CPX", CPX, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0xEC, "CPX", CPX, ABS, AddressModeKind.Abs, 4);

        SetInstruction(0xC0, "CPY", CPY, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0xC4, "CPY", CPY, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0xCC, "CPY", CPY, ABS, AddressModeKind.Abs, 4);

        SetInstruction(0x24, "BIT", BIT, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0x2C, "BIT", BIT, ABS, AddressModeKind.Abs, 4);

        SetInstruction(0x4C, "JMP", JMP, ABS, AddressModeKind.Abs, 3);
        SetInstruction(0x6C, "JMP", JMP, IND, AddressModeKind.Ind, 5);
        SetInstruction(0x20, "JSR", JSR, JSRADDR, AddressModeKind.Jsr, 6);
        SetInstruction(0x60, "RTS", RTS, IMP, AddressModeKind.Imp, 6);
        SetInstruction(0x40, "RTI", RTI, IMP, AddressModeKind.Imp, 6);

        SetInstruction(0x90, "BCC", BCC, REL, AddressModeKind.Rel, 2);
        SetInstruction(0xB0, "BCS", BCS, REL, AddressModeKind.Rel, 2);
        SetInstruction(0xF0, "BEQ", BEQ, REL, AddressModeKind.Rel, 2);
        SetInstruction(0x30, "BMI", BMI, REL, AddressModeKind.Rel, 2);
        SetInstruction(0xD0, "BNE", BNE, REL, AddressModeKind.Rel, 2);
        SetInstruction(0x10, "BPL", BPL, REL, AddressModeKind.Rel, 2);
        SetInstruction(0x50, "BVC", BVC, REL, AddressModeKind.Rel, 2);
        SetInstruction(0x70, "BVS", BVS, REL, AddressModeKind.Rel, 2);

        SetInstruction(0x18, "CLC", CLC, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0xD8, "CLD", CLD, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0x58, "CLI", CLI, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0xB8, "CLV", CLV, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0x38, "SEC", SEC, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0xF8, "SED", SED, IMP, AddressModeKind.Imp, 2);
        SetInstruction(0x78, "SEI", SEI, IMP, AddressModeKind.Imp, 2);

        // Unofficial opcodes (NMOS 6502 / NES-compatible behavior)
        SetInstruction(0x07, "SLO", SLO, ZP0, AddressModeKind.Zp0, 5);
        SetInstruction(0x17, "SLO", SLO, ZPX, AddressModeKind.Zpx, 6);
        SetInstruction(0x0F, "SLO", SLO, ABS, AddressModeKind.Abs, 6);
        SetInstruction(0x1F, "SLO", SLO, ABX, AddressModeKind.Abx, 7);
        SetInstruction(0x1B, "SLO", SLO, ABY, AddressModeKind.Aby, 7);
        SetInstruction(0x03, "SLO", SLO, IZX, AddressModeKind.Izx, 8);
        SetInstruction(0x13, "SLO", SLO, IZY, AddressModeKind.Izy, 8);

        SetInstruction(0x27, "RLA", RLA, ZP0, AddressModeKind.Zp0, 5);
        SetInstruction(0x37, "RLA", RLA, ZPX, AddressModeKind.Zpx, 6);
        SetInstruction(0x2F, "RLA", RLA, ABS, AddressModeKind.Abs, 6);
        SetInstruction(0x3F, "RLA", RLA, ABX, AddressModeKind.Abx, 7);
        SetInstruction(0x3B, "RLA", RLA, ABY, AddressModeKind.Aby, 7);
        SetInstruction(0x23, "RLA", RLA, IZX, AddressModeKind.Izx, 8);
        SetInstruction(0x33, "RLA", RLA, IZY, AddressModeKind.Izy, 8);

        SetInstruction(0x47, "SRE", SRE, ZP0, AddressModeKind.Zp0, 5);
        SetInstruction(0x57, "SRE", SRE, ZPX, AddressModeKind.Zpx, 6);
        SetInstruction(0x4F, "SRE", SRE, ABS, AddressModeKind.Abs, 6);
        SetInstruction(0x5F, "SRE", SRE, ABX, AddressModeKind.Abx, 7);
        SetInstruction(0x5B, "SRE", SRE, ABY, AddressModeKind.Aby, 7);
        SetInstruction(0x43, "SRE", SRE, IZX, AddressModeKind.Izx, 8);
        SetInstruction(0x53, "SRE", SRE, IZY, AddressModeKind.Izy, 8);

        SetInstruction(0x67, "RRA", RRA, ZP0, AddressModeKind.Zp0, 5);
        SetInstruction(0x77, "RRA", RRA, ZPX, AddressModeKind.Zpx, 6);
        SetInstruction(0x6F, "RRA", RRA, ABS, AddressModeKind.Abs, 6);
        SetInstruction(0x7F, "RRA", RRA, ABX, AddressModeKind.Abx, 7);
        SetInstruction(0x7B, "RRA", RRA, ABY, AddressModeKind.Aby, 7);
        SetInstruction(0x63, "RRA", RRA, IZX, AddressModeKind.Izx, 8);
        SetInstruction(0x73, "RRA", RRA, IZY, AddressModeKind.Izy, 8);

        SetInstruction(0x87, "SAX", SAX, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0x97, "SAX", SAX, ZPY, AddressModeKind.Zpy, 4);
        SetInstruction(0x8F, "SAX", SAX, ABS, AddressModeKind.Abs, 4);
        SetInstruction(0x83, "SAX", SAX, IZX, AddressModeKind.Izx, 6);

        SetInstruction(0xA7, "LAX", LAX, ZP0, AddressModeKind.Zp0, 3);
        SetInstruction(0xB7, "LAX", LAX, ZPY, AddressModeKind.Zpy, 4);
        SetInstruction(0xAF, "LAX", LAX, ABS, AddressModeKind.Abs, 4);
        SetInstruction(0xBF, "LAX", LAX, ABY, AddressModeKind.Aby, 4);
        SetInstruction(0xA3, "LAX", LAX, IZX, AddressModeKind.Izx, 6);
        SetInstruction(0xB3, "LAX", LAX, IZY, AddressModeKind.Izy, 5);

        SetInstruction(0xC7, "DCP", DCP, ZP0, AddressModeKind.Zp0, 5);
        SetInstruction(0xD7, "DCP", DCP, ZPX, AddressModeKind.Zpx, 6);
        SetInstruction(0xCF, "DCP", DCP, ABS, AddressModeKind.Abs, 6);
        SetInstruction(0xDF, "DCP", DCP, ABX, AddressModeKind.Abx, 7);
        SetInstruction(0xDB, "DCP", DCP, ABY, AddressModeKind.Aby, 7);
        SetInstruction(0xC3, "DCP", DCP, IZX, AddressModeKind.Izx, 8);
        SetInstruction(0xD3, "DCP", DCP, IZY, AddressModeKind.Izy, 8);

        SetInstruction(0xE7, "ISC", ISC, ZP0, AddressModeKind.Zp0, 5);
        SetInstruction(0xF7, "ISC", ISC, ZPX, AddressModeKind.Zpx, 6);
        SetInstruction(0xEF, "ISC", ISC, ABS, AddressModeKind.Abs, 6);
        SetInstruction(0xFF, "ISC", ISC, ABX, AddressModeKind.Abx, 7);
        SetInstruction(0xFB, "ISC", ISC, ABY, AddressModeKind.Aby, 7);
        SetInstruction(0xE3, "ISC", ISC, IZX, AddressModeKind.Izx, 8);
        SetInstruction(0xF3, "ISC", ISC, IZY, AddressModeKind.Izy, 8);

        SetInstruction(0x0B, "ANC", ANC, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0x2B, "ANC", ANC, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0x4B, "ASR", ASR, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0x6B, "ARR", ARR, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0x8B, "ANE", ANE, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0xAB, "LXA", LXA, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0xCB, "AXS", AXS, IMM, AddressModeKind.Imm, 2);
        SetInstruction(0xEB, "SBC", SBC, IMM, AddressModeKind.Imm, 2);

        SetInstruction(0x9F, "SHA", SHA, ABY, AddressModeKind.Aby, 5);
        SetInstruction(0x93, "SHA", SHA, IZY, AddressModeKind.Izy, 6);
        SetInstruction(0x9E, "SHX", SHX, ABY, AddressModeKind.Aby, 5);
        SetInstruction(0x9C, "SHY", SHY, ABX, AddressModeKind.Abx, 5);
        SetInstruction(0x9B, "SHS", SHS, ABY, AddressModeKind.Aby, 5);
        SetInstruction(0xBB, "LAE", LAE, ABY, AddressModeKind.Aby, 4);
    }

    private void SetInstruction(byte opcode, string name, OperationHandler operation, AddressModeHandler addressMode, AddressModeKind addressModeKind, byte cycles)
    {
        _lookup[opcode] = new Instruction(name, operation, addressMode, addressModeKind, cycles);
    }

    private byte Fetch()
    {
        if (_currentInstruction.AddressModeKind == AddressModeKind.Imp)
        {
            return _fetched;
        }

        if (_currentInstruction.AddressModeKind == AddressModeKind.Acc)
        {
            _fetched = A;
            return _fetched;
        }

        _fetched = Read(_addressAbsolute);
        return _fetched;
    }

    private bool GetFlag(StatusFlag flag)
    {
        return (Status & (byte)flag) != 0;
    }

    private void SetFlag(StatusFlag flag, bool value)
    {
        if (value)
        {
            Status |= (byte)flag;
        }
        else
        {
            Status &= (byte)~flag;
        }
    }

    private void SetZeroAndNegativeFlags(byte value)
    {
        SetFlag(StatusFlag.Zero, value == 0x00);
        SetFlag(StatusFlag.Negative, (value & 0x80) != 0);
    }

    private void ExecuteAdc(byte value)
    {
        var temp = (ushort)(A + value + (GetFlag(StatusFlag.Carry) ? 1 : 0));

        SetFlag(StatusFlag.Carry, temp > 0xFF);
        SetFlag(StatusFlag.Zero, (temp & 0x00FF) == 0);
        SetFlag(StatusFlag.Overflow, (~(A ^ value) & (A ^ temp) & 0x80) != 0);
        SetFlag(StatusFlag.Negative, (temp & 0x80) != 0);

        A = (byte)(temp & 0x00FF);
    }

    private void ExecuteSbc(byte value)
    {
        var inverted = (byte)(value ^ 0xFF);
        var temp = (ushort)(A + inverted + (GetFlag(StatusFlag.Carry) ? 1 : 0));

        SetFlag(StatusFlag.Carry, (temp & 0xFF00) != 0);
        SetFlag(StatusFlag.Zero, (temp & 0x00FF) == 0);
        SetFlag(StatusFlag.Overflow, ((temp ^ A) & (temp ^ inverted) & 0x80) != 0);
        SetFlag(StatusFlag.Negative, (temp & 0x80) != 0);

        A = (byte)(temp & 0x00FF);
    }

    private void DoIndexedStoreDummyRead()
    {
        if (_currentInstruction.AddressModeKind is AddressModeKind.Abx or AddressModeKind.Aby or AddressModeKind.Izy)
        {
            _ = Read((ushort)((_addressAbsoluteBase & 0xFF00) | (_addressAbsolute & 0x00FF)));
        }
    }

    private byte GetStoreHighMask()
    {
        // SH* family uses the high byte from the pre-carry base address.
        return (byte)(((_shBaseAddress >> 8) + 1) & 0xFF);
    }

    private static bool IsShMicroOpcode(byte opcode)
    {
        return opcode is 0x93 or 0x9F or 0x9B or 0x9C or 0x9E;
    }

    private bool IsShWriteStep()
    {
        return _shMicroOpcode == 0x93 ? _shMicroStep == 5 : _shMicroStep == 4;
    }

    private bool IsShTwoCyclesBeforeWriteStep()
    {
        return _shMicroOpcode == 0x93 ? _shMicroStep == 3 : _shMicroStep == 2;
    }

    private void StartShMicrocode(byte opcode)
    {
        _shMicroActive = true;
        _shMicroOpcode = opcode;
        _shMicroStep = 1;
        _shZeroPagePointer = 0;
        _shBaseAddress = 0;
        _shEffectiveAddress = 0;
        _shRawRegisterValue = 0;
        _shHighMask = 0;
        _shWriteValue = 0;
        _shRdyLowTwoCyclesBeforeWrite = false;
    }

    private ushort GetShStoreAddress()
    {
        var pageCrossed = (_shEffectiveAddress & 0xFF00) != (_shBaseAddress & 0xFF00);
        if (!pageCrossed)
        {
            return _shEffectiveAddress;
        }

        // SH* unstable stores can corrupt the write-address high byte on page-cross:
        // ABH <- ABH & mask.
        var effectiveHigh = (byte)(_shEffectiveAddress >> 8);
        var baseHigh = (byte)(_shBaseAddress >> 8);
        var sourceHigh = _shRdyLowTwoCyclesBeforeWrite ? baseHigh : effectiveHigh;
        var high = (byte)(sourceHigh & _shRawRegisterValue);
        return (ushort)((_shEffectiveAddress & 0x00FF) | (high << 8));
    }

    private void ComputeShRawAndValue()
    {
        _shRawRegisterValue = _shMicroOpcode switch
        {
            0x9C => Y,
            0x9E => X,
            _ => (byte)(A & X)
        };

        _shHighMask = GetStoreHighMask();
        _shWriteValue = (byte)(_shRawRegisterValue & _shHighMask);
        if (_shMicroOpcode == 0x9B)
        {
            StackPointer = (byte)(A & X);
        }
    }

    private void RunShMicroStep()
    {
        if (_shMicroOpcode == 0x93)
        {
            switch (_shMicroStep)
            {
                case 1:
                    _shZeroPagePointer = Read(ProgramCounter++);
                    break;
                case 2:
                {
                    var low = Read(_shZeroPagePointer);
                    _shBaseAddress = (ushort)((_shBaseAddress & 0xFF00) | low);
                    break;
                }
                case 3:
                {
                    var high = Read((byte)(_shZeroPagePointer + 1));
                    _shBaseAddress = (ushort)((high << 8) | (_shBaseAddress & 0x00FF));
                    _shEffectiveAddress = (ushort)(_shBaseAddress + Y);
                    ComputeShRawAndValue();
                    break;
                }
                case 4:
                    _ = Read((ushort)((_shBaseAddress & 0xFF00) | (_shEffectiveAddress & 0x00FF)));
                    break;
                case 5:
                    Write(GetShStoreAddress(), _shWriteValue);
                    _shMicroActive = false;
                    break;
            }
        }
        else
        {
            var index = _shMicroOpcode == 0x9C ? X : Y;
            switch (_shMicroStep)
            {
                case 1:
                {
                    var low = Read(ProgramCounter++);
                    _shBaseAddress = (ushort)((_shBaseAddress & 0xFF00) | low);
                    break;
                }
                case 2:
                {
                    var high = Read(ProgramCounter++);
                    _shBaseAddress = (ushort)((high << 8) | (_shBaseAddress & 0x00FF));
                    _shEffectiveAddress = (ushort)(_shBaseAddress + index);
                    ComputeShRawAndValue();
                    break;
                }
                case 3:
                    _ = Read((ushort)((_shBaseAddress & 0xFF00) | (_shEffectiveAddress & 0x00FF)));
                    break;
                case 4:
                    Write(GetShStoreAddress(), _shWriteValue);
                    _shMicroActive = false;
                    break;
            }
        }

        if (_shMicroActive)
        {
            _shMicroStep++;
        }
    }

    private void Push(byte value)
    {
        Write((ushort)(0x0100 + StackPointer), value);
        StackPointer--;
    }

    private byte Pop()
    {
        StackPointer++;
        return Read((ushort)(0x0100 + StackPointer));
    }

    private void HandleNmi()
    {
        Push((byte)((ProgramCounter >> 8) & 0x00FF));
        Push((byte)(ProgramCounter & 0x00FF));

        var statusToPush = (byte)((Status & ~(byte)StatusFlag.Break) | (byte)StatusFlag.Unused);
        Push(statusToPush);
        SetFlag(StatusFlag.InterruptDisable, true);

        var low = Read(0xFFFA);
        var high = Read(0xFFFB);
        ProgramCounter = (ushort)((high << 8) | low);
    }

    private void HandleIrq()
    {
        Push((byte)((ProgramCounter >> 8) & 0x00FF));
        Push((byte)(ProgramCounter & 0x00FF));

        var statusToPush = (byte)((Status & ~(byte)StatusFlag.Break) | (byte)StatusFlag.Unused);
        Push(statusToPush);
        SetFlag(StatusFlag.InterruptDisable, true);

        var low = Read(0xFFFE);
        var high = Read(0xFFFF);
        ProgramCounter = (ushort)((high << 8) | low);
    }

    private void BranchIf(bool condition)
    {
        PollIrq(); // branch poll before cycle 2

        if (!condition)
        {
            return;
        }

        _cyclesRemaining++;
        var target = (ushort)(ProgramCounter + _addressRelative);
        if ((target & 0xFF00) != (ProgramCounter & 0xFF00))
        {
            _cyclesRemaining++;
            PollIrq(); // taken + page cross: poll before cycle 4
        }

        ProgramCounter = target;
    }

    private byte IMP()
    {
        _fetched = A;
        return 0;
    }

    private byte ACC()
    {
        return 0;
    }

    private byte IMM()
    {
        _addressAbsolute = ProgramCounter++;
        _addressAbsoluteBase = _addressAbsolute;
        return 0;
    }

    private byte ZP0()
    {
        _addressAbsolute = Read(ProgramCounter++);
        _addressAbsolute &= 0x00FF;
        _addressAbsoluteBase = _addressAbsolute;
        return 0;
    }

    private byte ZPX()
    {
        _addressAbsolute = (ushort)((Read(ProgramCounter++) + X) & 0x00FF);
        _addressAbsoluteBase = _addressAbsolute;
        return 0;
    }

    private byte ZPY()
    {
        _addressAbsolute = (ushort)((Read(ProgramCounter++) + Y) & 0x00FF);
        _addressAbsoluteBase = _addressAbsolute;
        return 0;
    }

    private byte REL()
    {
        _addressRelative = Read(ProgramCounter++);
        if ((_addressRelative & 0x80) != 0)
        {
            _addressRelative |= 0xFF00;
        }

        return 0;
    }

    private byte ABS()
    {
        var low = Read(ProgramCounter++);
        var high = Read(ProgramCounter++);
        _addressAbsolute = (ushort)((high << 8) | low);
        _addressAbsoluteBase = _addressAbsolute;
        return 0;
    }

    private byte ABX()
    {
        var low = Read(ProgramCounter++);
        var high = Read(ProgramCounter++);
        var baseAddress = (ushort)((high << 8) | low);
        _addressAbsoluteBase = baseAddress;

        _addressAbsolute = (ushort)(baseAddress + X);
        var pageCrossed = (_addressAbsolute & 0xFF00) != (baseAddress & 0xFF00);
        if (pageCrossed)
        {
            _ = Read((ushort)((baseAddress & 0xFF00) | (_addressAbsolute & 0x00FF)));
        }

        return (byte)(pageCrossed ? 1 : 0);
    }

    private byte ABY()
    {
        var low = Read(ProgramCounter++);
        var high = Read(ProgramCounter++);
        var baseAddress = (ushort)((high << 8) | low);
        _addressAbsoluteBase = baseAddress;

        _addressAbsolute = (ushort)(baseAddress + Y);
        var pageCrossed = (_addressAbsolute & 0xFF00) != (baseAddress & 0xFF00);
        if (pageCrossed)
        {
            _ = Read((ushort)((baseAddress & 0xFF00) | (_addressAbsolute & 0x00FF)));
        }

        return (byte)(pageCrossed ? 1 : 0);
    }

    private byte IND()
    {
        var ptrLow = Read(ProgramCounter++);
        var ptrHigh = Read(ProgramCounter++);
        var pointer = (ushort)((ptrHigh << 8) | ptrLow);

        if (ptrLow == 0xFF)
        {
            var low = Read(pointer);
            var high = Read((ushort)(pointer & 0xFF00));
            _addressAbsolute = (ushort)((high << 8) | low);
            _addressAbsoluteBase = _addressAbsolute;
            return 0;
        }

        {
            var low = Read(pointer);
            var high = Read((ushort)(pointer + 1));
            _addressAbsolute = (ushort)((high << 8) | low);
            _addressAbsoluteBase = _addressAbsolute;
            return 0;
        }
    }

    private byte IZX()
    {
        var t = (byte)(Read(ProgramCounter++) + X);
        var low = Read(t);
        var high = Read((byte)(t + 1));
        _addressAbsolute = (ushort)((high << 8) | low);
        _addressAbsoluteBase = _addressAbsolute;
        return 0;
    }

    private byte IZY()
    {
        var t = Read(ProgramCounter++);
        var low = Read(t);
        var high = Read((byte)(t + 1));
        var baseAddress = (ushort)((high << 8) | low);
        _addressAbsoluteBase = baseAddress;

        _addressAbsolute = (ushort)(baseAddress + Y);
        var pageCrossed = (_addressAbsolute & 0xFF00) != (baseAddress & 0xFF00);
        if (pageCrossed)
        {
            _ = Read((ushort)((baseAddress & 0xFF00) | (_addressAbsolute & 0x00FF)));
        }

        return (byte)(pageCrossed ? 1 : 0);
    }

    private byte JSRADDR()
    {
        // JSR reads the low target byte first, then performs stack activity,
        // and only then reads the high target byte.
        _addressAbsolute = Read(ProgramCounter++);
        _addressAbsoluteBase = _addressAbsolute;
        return 0;
    }

    private byte ADC()
    {
        ExecuteAdc(Fetch());
        return 1;
    }

    private byte SBC()
    {
        ExecuteSbc(Fetch());
        return 1;
    }

    private byte AND()
    {
        A &= Fetch();
        SetZeroAndNegativeFlags(A);
        return 1;
    }

    private byte ORA()
    {
        A |= Fetch();
        SetZeroAndNegativeFlags(A);
        return 1;
    }

    private byte EOR()
    {
        A ^= Fetch();
        SetZeroAndNegativeFlags(A);
        return 1;
    }

    private byte LDA()
    {
        A = Fetch();
        SetZeroAndNegativeFlags(A);
        return 1;
    }

    private byte LDX()
    {
        X = Fetch();
        SetZeroAndNegativeFlags(X);
        return 1;
    }

    private byte LDY()
    {
        Y = Fetch();
        SetZeroAndNegativeFlags(Y);
        return 1;
    }

    private byte STA()
    {
        DoIndexedStoreDummyRead();
        Write(_addressAbsolute, A);
        return 0;
    }

    private byte STX()
    {
        Write(_addressAbsolute, X);
        return 0;
    }

    private byte STY()
    {
        Write(_addressAbsolute, Y);
        return 0;
    }

    private byte TAX()
    {
        X = A;
        SetZeroAndNegativeFlags(X);
        return 0;
    }

    private byte TAY()
    {
        Y = A;
        SetZeroAndNegativeFlags(Y);
        return 0;
    }

    private byte TXA()
    {
        A = X;
        SetZeroAndNegativeFlags(A);
        return 0;
    }

    private byte TYA()
    {
        A = Y;
        SetZeroAndNegativeFlags(A);
        return 0;
    }

    private byte TSX()
    {
        X = StackPointer;
        SetZeroAndNegativeFlags(X);
        return 0;
    }

    private byte TXS()
    {
        StackPointer = X;
        return 0;
    }

    private byte PHA()
    {
        Push(A);
        return 0;
    }

    private byte PLA()
    {
        A = Pop();
        SetZeroAndNegativeFlags(A);
        return 0;
    }

    private byte PHP()
    {
        Push((byte)(Status | (byte)StatusFlag.Break | (byte)StatusFlag.Unused));
        return 0;
    }

    private byte PLP()
    {
        PollIrq(); // PLP polls before cycle 2 (before pulling P)
        Status = Pop();
        SetFlag(StatusFlag.Unused, true);
        return 0;
    }

    private byte ASL()
    {
        var value = Fetch();
        var temp = (ushort)(value << 1);

        SetFlag(StatusFlag.Carry, (temp & 0xFF00) != 0);
        var result = (byte)(temp & 0x00FF);
        SetZeroAndNegativeFlags(result);

        if (_currentInstruction.AddressModeKind == AddressModeKind.Acc)
        {
            A = result;
        }
        else
        {
            Write(_addressAbsolute, value);
            Write(_addressAbsolute, result);
        }

        return 0;
    }

    private byte LSR()
    {
        var value = Fetch();
        SetFlag(StatusFlag.Carry, (value & 0x01) != 0);
        var result = (byte)(value >> 1);
        SetZeroAndNegativeFlags(result);

        if (_currentInstruction.AddressModeKind == AddressModeKind.Acc)
        {
            A = result;
        }
        else
        {
            Write(_addressAbsolute, value);
            Write(_addressAbsolute, result);
        }

        return 0;
    }

    private byte ROL()
    {
        var value = Fetch();
        var temp = (ushort)((value << 1) | (GetFlag(StatusFlag.Carry) ? 1 : 0));

        SetFlag(StatusFlag.Carry, (temp & 0xFF00) != 0);
        var result = (byte)(temp & 0x00FF);
        SetZeroAndNegativeFlags(result);

        if (_currentInstruction.AddressModeKind == AddressModeKind.Acc)
        {
            A = result;
        }
        else
        {
            Write(_addressAbsolute, value);
            Write(_addressAbsolute, result);
        }

        return 0;
    }

    private byte ROR()
    {
        var value = Fetch();
        var result = (byte)(((GetFlag(StatusFlag.Carry) ? 1 : 0) << 7) | (value >> 1));

        SetFlag(StatusFlag.Carry, (value & 0x01) != 0);
        SetZeroAndNegativeFlags(result);

        if (_currentInstruction.AddressModeKind == AddressModeKind.Acc)
        {
            A = result;
        }
        else
        {
            Write(_addressAbsolute, value);
            Write(_addressAbsolute, result);
        }

        return 0;
    }

    private byte SLO()
    {
        var oldValue = Fetch();
        var result = (byte)(oldValue << 1);
        SetFlag(StatusFlag.Carry, (oldValue & 0x80) != 0);
        Write(_addressAbsolute, oldValue);
        Write(_addressAbsolute, result);
        A |= result;
        SetZeroAndNegativeFlags(A);
        return 0;
    }

    private byte RLA()
    {
        var oldValue = Fetch();
        var result = (byte)((oldValue << 1) | (GetFlag(StatusFlag.Carry) ? 1 : 0));
        SetFlag(StatusFlag.Carry, (oldValue & 0x80) != 0);
        Write(_addressAbsolute, oldValue);
        Write(_addressAbsolute, result);
        A &= result;
        SetZeroAndNegativeFlags(A);
        return 0;
    }

    private byte SRE()
    {
        var oldValue = Fetch();
        var result = (byte)(oldValue >> 1);
        SetFlag(StatusFlag.Carry, (oldValue & 0x01) != 0);
        Write(_addressAbsolute, oldValue);
        Write(_addressAbsolute, result);
        A ^= result;
        SetZeroAndNegativeFlags(A);
        return 0;
    }

    private byte RRA()
    {
        var oldValue = Fetch();
        var result = (byte)(((GetFlag(StatusFlag.Carry) ? 1 : 0) << 7) | (oldValue >> 1));
        SetFlag(StatusFlag.Carry, (oldValue & 0x01) != 0);
        Write(_addressAbsolute, oldValue);
        Write(_addressAbsolute, result);
        ExecuteAdc(result);
        return 0;
    }

    private byte SAX()
    {
        Write(_addressAbsolute, (byte)(A & X));
        return 0;
    }

    private byte LAX()
    {
        var value = Fetch();
        A = value;
        X = value;
        SetZeroAndNegativeFlags(value);
        return 1;
    }

    private byte DCP()
    {
        var oldValue = Fetch();
        var newValue = (byte)(oldValue - 1);
        Write(_addressAbsolute, oldValue);
        Write(_addressAbsolute, newValue);

        var temp = (ushort)(A - newValue);
        SetFlag(StatusFlag.Carry, A >= newValue);
        SetFlag(StatusFlag.Zero, (temp & 0x00FF) == 0);
        SetFlag(StatusFlag.Negative, (temp & 0x0080) != 0);
        return 0;
    }

    private byte ISC()
    {
        var oldValue = Fetch();
        var newValue = (byte)(oldValue + 1);
        Write(_addressAbsolute, oldValue);
        Write(_addressAbsolute, newValue);
        ExecuteSbc(newValue);
        return 0;
    }

    private byte ANC()
    {
        A &= Fetch();
        SetZeroAndNegativeFlags(A);
        SetFlag(StatusFlag.Carry, (A & 0x80) != 0);
        return 0;
    }

    private byte ASR()
    {
        var value = (byte)(A & Fetch());
        SetFlag(StatusFlag.Carry, (value & 0x01) != 0);
        A = (byte)(value >> 1);
        SetZeroAndNegativeFlags(A);
        return 0;
    }

    private byte ARR()
    {
        var value = (byte)(A & Fetch());
        var result = (byte)(((GetFlag(StatusFlag.Carry) ? 1 : 0) << 7) | (value >> 1));
        A = result;
        SetZeroAndNegativeFlags(A);
        SetFlag(StatusFlag.Carry, (A & 0x40) != 0);
        SetFlag(StatusFlag.Overflow, ((A >> 6) & 0x01) != ((A >> 5) & 0x01));
        return 0;
    }

    private byte ANE()
    {
        A = (byte)(A & X & Fetch());
        SetZeroAndNegativeFlags(A);
        return 0;
    }

    private byte LXA()
    {
        A = (byte)(A & Fetch());
        X = A;
        SetZeroAndNegativeFlags(A);
        return 0;
    }

    private byte AXS()
    {
        var value = Fetch();
        var anded = (byte)(A & X);
        var result = (byte)(anded - value);
        SetFlag(StatusFlag.Carry, anded >= value);
        X = result;
        SetZeroAndNegativeFlags(X);
        return 0;
    }

    private byte SHA()
    {
        return 0;
    }

    private byte SHX()
    {
        return 0;
    }

    private byte SHY()
    {
        return 0;
    }

    private byte SHS()
    {
        return 0;
    }

    private byte LAE()
    {
        var value = (byte)(Fetch() & StackPointer);
        A = value;
        X = value;
        StackPointer = value;
        SetZeroAndNegativeFlags(value);
        return 1;
    }

    private byte INC()
    {
        var oldValue = Fetch();
        var newValue = (byte)(oldValue + 1);
        Write(_addressAbsolute, oldValue);
        Write(_addressAbsolute, newValue);
        SetZeroAndNegativeFlags(newValue);
        return 0;
    }

    private byte DEC()
    {
        var oldValue = Fetch();
        var newValue = (byte)(oldValue - 1);
        Write(_addressAbsolute, oldValue);
        Write(_addressAbsolute, newValue);
        SetZeroAndNegativeFlags(newValue);
        return 0;
    }

    private byte INX()
    {
        X++;
        SetZeroAndNegativeFlags(X);
        return 0;
    }

    private byte DEX()
    {
        X--;
        SetZeroAndNegativeFlags(X);
        return 0;
    }

    private byte INY()
    {
        Y++;
        SetZeroAndNegativeFlags(Y);
        return 0;
    }

    private byte DEY()
    {
        Y--;
        SetZeroAndNegativeFlags(Y);
        return 0;
    }

    private byte CMP()
    {
        var value = Fetch();
        var temp = (ushort)(A - value);

        SetFlag(StatusFlag.Carry, A >= value);
        SetFlag(StatusFlag.Zero, (temp & 0x00FF) == 0);
        SetFlag(StatusFlag.Negative, (temp & 0x0080) != 0);
        return 1;
    }

    private byte CPX()
    {
        var value = Fetch();
        var temp = (ushort)(X - value);

        SetFlag(StatusFlag.Carry, X >= value);
        SetFlag(StatusFlag.Zero, (temp & 0x00FF) == 0);
        SetFlag(StatusFlag.Negative, (temp & 0x0080) != 0);
        return 0;
    }

    private byte CPY()
    {
        var value = Fetch();
        var temp = (ushort)(Y - value);

        SetFlag(StatusFlag.Carry, Y >= value);
        SetFlag(StatusFlag.Zero, (temp & 0x00FF) == 0);
        SetFlag(StatusFlag.Negative, (temp & 0x0080) != 0);
        return 0;
    }

    private byte BIT()
    {
        var value = Fetch();
        var temp = (byte)(A & value);

        SetFlag(StatusFlag.Zero, temp == 0);
        SetFlag(StatusFlag.Overflow, (value & 0x40) != 0);
        SetFlag(StatusFlag.Negative, (value & 0x80) != 0);
        return 0;
    }

    private byte JMP()
    {
        ProgramCounter = _addressAbsolute;
        return 0;
    }

    private byte JSR()
    {
        // Dummy read from stack page before the return address pushes.
        _ = Read((ushort)(0x0100 + StackPointer));

        Push((byte)((ProgramCounter >> 8) & 0x00FF));
        Push((byte)(ProgramCounter & 0x00FF));

        var high = Read(ProgramCounter++);
        ProgramCounter = (ushort)((high << 8) | (_addressAbsolute & 0x00FF));
        return 0;
    }

    private byte RTS()
    {
        var low = Pop();
        var high = Pop();
        ProgramCounter = (ushort)(((high << 8) | low) + 1);
        return 0;
    }

    private byte RTI()
    {
        Status = Pop();
        SetFlag(StatusFlag.Break, false);
        SetFlag(StatusFlag.Unused, true);

        var low = Pop();
        var high = Pop();
        ProgramCounter = (ushort)((high << 8) | low);
        return 0;
    }

    private byte BCC()
    {
        BranchIf(!GetFlag(StatusFlag.Carry));
        return 0;
    }

    private byte BCS()
    {
        BranchIf(GetFlag(StatusFlag.Carry));
        return 0;
    }

    private byte BEQ()
    {
        BranchIf(GetFlag(StatusFlag.Zero));
        return 0;
    }

    private byte BMI()
    {
        BranchIf(GetFlag(StatusFlag.Negative));
        return 0;
    }

    private byte BNE()
    {
        BranchIf(!GetFlag(StatusFlag.Zero));
        return 0;
    }

    private byte BPL()
    {
        BranchIf(!GetFlag(StatusFlag.Negative));
        return 0;
    }

    private byte BVC()
    {
        BranchIf(!GetFlag(StatusFlag.Overflow));
        return 0;
    }

    private byte BVS()
    {
        BranchIf(GetFlag(StatusFlag.Overflow));
        return 0;
    }

    private byte CLC()
    {
        SetFlag(StatusFlag.Carry, false);
        return 0;
    }

    private byte CLD()
    {
        SetFlag(StatusFlag.Decimal, false);
        return 0;
    }

    private byte CLI()
    {
        PollIrq(); // CLI polls before cycle 2 (before clearing I)
        SetFlag(StatusFlag.InterruptDisable, false);
        return 0;
    }

    private byte CLV()
    {
        SetFlag(StatusFlag.Overflow, false);
        return 0;
    }

    private byte SEC()
    {
        SetFlag(StatusFlag.Carry, true);
        return 0;
    }

    private byte SED()
    {
        SetFlag(StatusFlag.Decimal, true);
        return 0;
    }

    private byte SEI()
    {
        PollIrq(); // SEI polls before cycle 2 (before setting I)
        SetFlag(StatusFlag.InterruptDisable, true);
        return 0;
    }

    private byte BRK()
    {
        ProgramCounter++;

        Push((byte)((ProgramCounter >> 8) & 0x00FF));
        Push((byte)(ProgramCounter & 0x00FF));

        SetFlag(StatusFlag.Break, true);
        Push(Status);
        SetFlag(StatusFlag.Break, false);

        SetFlag(StatusFlag.InterruptDisable, true);

        var low = Read(0xFFFE);
        var high = Read(0xFFFF);
        ProgramCounter = (ushort)((high << 8) | low);
        return 0;
    }

    private byte NOP()
    {
        if (_currentInstruction.AddressModeKind is not AddressModeKind.Imp and not AddressModeKind.Acc)
        {
            _ = Fetch();
        }

        return 0;
    }

    private byte XXX()
    {
        return 0;
    }
}


