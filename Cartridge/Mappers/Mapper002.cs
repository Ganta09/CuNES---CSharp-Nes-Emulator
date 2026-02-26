namespace cunes.Cartridge.Mappers;

internal sealed class Mapper002 : IMapper
{
    private readonly int _prgBankCount;
    private byte _bankSelect;

    public Mapper002(int prgBankCount, MirroringMode mirroring)
    {
        _prgBankCount = Math.Max(1, prgBankCount);
        Mirroring = mirroring;
        Reset();
    }

    public byte MapperId => 2;

    public MirroringMode Mirroring { get; }

    public void Reset()
    {
        _bankSelect = 0;
    }

    public bool CpuRead(ushort address, out int mappedAddress, out bool isPrgRam)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            mappedAddress = address - 0x6000;
            isPrgRam = true;
            return true;
        }

        if (address is >= 0x8000 and <= 0xBFFF)
        {
            var bank = _bankSelect % _prgBankCount;
            mappedAddress = bank * 0x4000 + (address - 0x8000);
            isPrgRam = false;
            return true;
        }

        if (address >= 0xC000)
        {
            mappedAddress = (_prgBankCount - 1) * 0x4000 + (address - 0xC000);
            isPrgRam = false;
            return true;
        }

        mappedAddress = -1;
        isPrgRam = false;
        return false;
    }

    public bool CpuWrite(ushort address, byte data, out int mappedAddress, out bool isPrgRam)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            mappedAddress = address - 0x6000;
            isPrgRam = true;
            return true;
        }

        if (address >= 0x8000)
        {
            _bankSelect = (byte)(data & 0x0F);
            mappedAddress = -1;
            isPrgRam = false;
            return true;
        }

        mappedAddress = -1;
        isPrgRam = false;
        return false;
    }

    public bool PpuRead(ushort address, out int mappedAddress)
    {
        if (address <= 0x1FFF)
        {
            mappedAddress = address;
            return true;
        }

        mappedAddress = -1;
        return false;
    }

    public bool PpuWrite(ushort address, out int mappedAddress)
    {
        return PpuRead(address, out mappedAddress);
    }
}
