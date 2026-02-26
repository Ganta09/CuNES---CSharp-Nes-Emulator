namespace cunes.Cartridge.Mappers;

internal sealed class Mapper000 : IMapper
{
    private readonly int _prgBankCount;

    public Mapper000(int prgBankCount, MirroringMode mirroring)
    {
        _prgBankCount = prgBankCount;
        Mirroring = mirroring;
    }

    public byte MapperId => 0;

    public MirroringMode Mirroring { get; }

    public void Reset()
    {
    }

    public bool CpuRead(ushort address, out int mappedAddress, out bool isPrgRam)
    {
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
        _ = data;

        if (address >= 0x8000)
        {
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
        if (address <= 0x1FFF)
        {
            mappedAddress = address;
            return true;
        }

        mappedAddress = -1;
        return false;
    }
}
