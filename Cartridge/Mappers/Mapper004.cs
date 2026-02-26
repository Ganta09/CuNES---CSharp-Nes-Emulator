namespace cunes.Cartridge.Mappers;

internal sealed class Mapper004 : IMapper
{
    private readonly int _prgBankCount8k;
    private readonly int _chrBankCount1k;
    private readonly byte[] _bankRegisters = new byte[8];

    private byte _bankSelect;
    private bool _prgRamEnabled;

    public Mapper004(int prgRomBytes, int chrRomBytes, MirroringMode mirroring)
    {
        _prgBankCount8k = Math.Max(1, prgRomBytes / 0x2000);
        _chrBankCount1k = Math.Max(8, chrRomBytes / 0x400);
        Mirroring = mirroring;
        Reset();
    }

    public byte MapperId => 4;

    public MirroringMode Mirroring { get; private set; }

    public void Reset()
    {
        Array.Clear(_bankRegisters);
        _bankSelect = 0;
        _prgRamEnabled = true;
    }

    public bool CpuRead(ushort address, out int mappedAddress, out bool isPrgRam)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            if (!_prgRamEnabled)
            {
                mappedAddress = -1;
                isPrgRam = false;
                return false;
            }

            mappedAddress = address - 0x6000;
            isPrgRam = true;
            return true;
        }

        if (address < 0x8000)
        {
            mappedAddress = -1;
            isPrgRam = false;
            return false;
        }

        var prgMode = (_bankSelect & 0x40) != 0;
        var lastBank = _prgBankCount8k - 1;
        var secondLastBank = Math.Max(0, _prgBankCount8k - 2);

        int selectedBank = address switch
        {
            <= 0x9FFF => prgMode ? secondLastBank : (_bankRegisters[6] & 0x3F),
            <= 0xBFFF => _bankRegisters[7] & 0x3F,
            <= 0xDFFF => prgMode ? (_bankRegisters[6] & 0x3F) : secondLastBank,
            _ => lastBank
        };

        selectedBank %= _prgBankCount8k;
        mappedAddress = selectedBank * 0x2000 + (address & 0x1FFF);
        isPrgRam = false;
        return true;
    }

    public bool CpuWrite(ushort address, byte data, out int mappedAddress, out bool isPrgRam)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            if (!_prgRamEnabled)
            {
                mappedAddress = -1;
                isPrgRam = false;
                return true;
            }

            mappedAddress = address - 0x6000;
            isPrgRam = true;
            return true;
        }

        if (address < 0x8000)
        {
            mappedAddress = -1;
            isPrgRam = false;
            return false;
        }

        if (address <= 0x9FFF)
        {
            if ((address & 0x0001) == 0)
            {
                _bankSelect = data;
            }
            else
            {
                _bankRegisters[_bankSelect & 0x07] = data;
            }
        }
        else if (address <= 0xBFFF)
        {
            if ((address & 0x0001) == 0)
            {
                Mirroring = (data & 0x01) == 0 ? MirroringMode.Vertical : MirroringMode.Horizontal;
            }
            else
            {
                _prgRamEnabled = (data & 0x80) != 0;
            }
        }

        mappedAddress = -1;
        isPrgRam = false;
        return true;
    }

    public bool PpuRead(ushort address, out int mappedAddress)
    {
        if (address > 0x1FFF)
        {
            mappedAddress = -1;
            return false;
        }

        var chrMode = (_bankSelect & 0x80) != 0;
        int bank1k;

        if (!chrMode)
        {
            bank1k = address switch
            {
                <= 0x03FF => _bankRegisters[0] & 0xFE,
                <= 0x07FF => (_bankRegisters[0] & 0xFE) + 1,
                <= 0x0BFF => _bankRegisters[1] & 0xFE,
                <= 0x0FFF => (_bankRegisters[1] & 0xFE) + 1,
                <= 0x13FF => _bankRegisters[2],
                <= 0x17FF => _bankRegisters[3],
                <= 0x1BFF => _bankRegisters[4],
                _ => _bankRegisters[5]
            };
        }
        else
        {
            bank1k = address switch
            {
                <= 0x03FF => _bankRegisters[2],
                <= 0x07FF => _bankRegisters[3],
                <= 0x0BFF => _bankRegisters[4],
                <= 0x0FFF => _bankRegisters[5],
                <= 0x13FF => _bankRegisters[0] & 0xFE,
                <= 0x17FF => (_bankRegisters[0] & 0xFE) + 1,
                <= 0x1BFF => _bankRegisters[1] & 0xFE,
                _ => (_bankRegisters[1] & 0xFE) + 1
            };
        }

        bank1k %= _chrBankCount1k;
        mappedAddress = bank1k * 0x400 + (address & 0x03FF);
        return true;
    }

    public bool PpuWrite(ushort address, out int mappedAddress)
    {
        return PpuRead(address, out mappedAddress);
    }
}
