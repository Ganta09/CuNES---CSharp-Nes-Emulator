namespace cunes.Cartridge.Mappers;

internal sealed class Mapper001 : IMapper
{
    private readonly int _prgBankCount;
    private readonly int _chrBankCount4k;

    private byte _shiftRegister;
    private byte _control;
    private byte _chrBank0;
    private byte _chrBank1;
    private byte _prgBank;

    public Mapper001(int prgBankCount, int chrBankCount8k, MirroringMode mirroring)
    {
        _prgBankCount = Math.Max(1, prgBankCount);
        _chrBankCount4k = Math.Max(2, chrBankCount8k * 2);
        Mirroring = mirroring;
        Reset();
    }

    public byte MapperId => 1;

    public MirroringMode Mirroring { get; private set; }

    public void Reset()
    {
        _shiftRegister = 0x10;
        _control = 0x1C;
        _chrBank0 = 0;
        _chrBank1 = 0;
        _prgBank = 0;
        UpdateMirroringFromControl();
    }

    public bool CpuRead(ushort address, out int mappedAddress, out bool isPrgRam)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            if ((_prgBank & 0x10) != 0)
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

        var prgMode = (_control >> 2) & 0x03;
        var prgBank = _prgBank & 0x0F;

        if (prgMode is 0 or 1)
        {
            var bank = (prgBank & 0x0E) % _prgBankCount;
            mappedAddress = bank * 0x4000 + (address - 0x8000);
        }
        else if (prgMode == 2)
        {
            mappedAddress = address <= 0xBFFF
                ? address - 0x8000
                : (prgBank % _prgBankCount) * 0x4000 + (address - 0xC000);
        }
        else
        {
            mappedAddress = address <= 0xBFFF
                ? (prgBank % _prgBankCount) * 0x4000 + (address - 0x8000)
                : (_prgBankCount - 1) * 0x4000 + (address - 0xC000);
        }

        isPrgRam = false;
        return true;
    }

    public bool CpuWrite(ushort address, byte data, out int mappedAddress, out bool isPrgRam)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            if ((_prgBank & 0x10) != 0)
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

        if ((data & 0x80) != 0)
        {
            _shiftRegister = 0x10;
            _control |= 0x0C;
            UpdateMirroringFromControl();
            mappedAddress = -1;
            isPrgRam = false;
            return true;
        }

        var complete = (_shiftRegister & 0x01) != 0;
        _shiftRegister >>= 1;
        _shiftRegister |= (byte)((data & 0x01) << 4);

        if (complete)
        {
            if (address <= 0x9FFF)
            {
                _control = (byte)(_shiftRegister & 0x1F);
                UpdateMirroringFromControl();
            }
            else if (address <= 0xBFFF)
            {
                _chrBank0 = (byte)(_shiftRegister & 0x1F);
            }
            else if (address <= 0xDFFF)
            {
                _chrBank1 = (byte)(_shiftRegister & 0x1F);
            }
            else
            {
                _prgBank = (byte)(_shiftRegister & 0x1F);
            }

            _shiftRegister = 0x10;
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

        var chrMode4k = (_control & 0x10) != 0;
        if (!chrMode4k)
        {
            var bank8k = (_chrBank0 & 0x1E) >> 1;
            mappedAddress = (bank8k * 0x2000 + address) % (_chrBankCount4k * 0x1000);
            return true;
        }

        if (address <= 0x0FFF)
        {
            mappedAddress = (_chrBank0 % _chrBankCount4k) * 0x1000 + address;
        }
        else
        {
            mappedAddress = (_chrBank1 % _chrBankCount4k) * 0x1000 + (address - 0x1000);
        }

        return true;
    }

    public bool PpuWrite(ushort address, out int mappedAddress)
    {
        return PpuRead(address, out mappedAddress);
    }

    private void UpdateMirroringFromControl()
    {
        Mirroring = (_control & 0x03) switch
        {
            0 => MirroringMode.OneScreenLower,
            1 => MirroringMode.OneScreenUpper,
            2 => MirroringMode.Vertical,
            _ => MirroringMode.Horizontal
        };
    }
}
