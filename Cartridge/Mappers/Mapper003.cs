namespace cunes.Cartridge.Mappers;

internal sealed class Mapper003 : IMapper
{
    private readonly int _prgBankCount;
    private readonly int _chrBankCount;
    private byte _chrBankSelect;

    public Mapper003(int prgBankCount, int chrBankCount, MirroringMode mirroring)
    {
        _prgBankCount = prgBankCount;
        _chrBankCount = Math.Max(1, chrBankCount);
        Mirroring = mirroring;
        Reset();
    }

    public byte MapperId => 3;

    public MirroringMode Mirroring { get; }

    public void Reset()
    {
        _chrBankSelect = 0;
    }

    public bool CpuRead(ushort address, out int mappedAddress, out bool isPrgRam)
    {
        if (address is >= 0x6000 and <= 0x7FFF)
        {
            mappedAddress = address - 0x6000;
            isPrgRam = true;
            return true;
        }

        if (address >= 0x8000)
        {
            mappedAddress = _prgBankCount > 1 ? address & 0x7FFF : address & 0x3FFF;
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
            _chrBankSelect = data;
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
        if (address > 0x1FFF)
        {
            mappedAddress = -1;
            return false;
        }

        var bank = _chrBankSelect % _chrBankCount;
        mappedAddress = bank * 0x2000 + address;
        return true;
    }

    public bool PpuWrite(ushort address, out int mappedAddress)
    {
        return PpuRead(address, out mappedAddress);
    }
}
