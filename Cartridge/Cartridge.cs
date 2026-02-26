using cunes.Cartridge.Mappers;

namespace cunes.Cartridge;

public sealed class Cartridge
{
    private readonly byte[] _prgRom;
    private readonly byte[] _prgRam = new byte[8 * 1024];
    private readonly byte[] _chrData;
    private readonly bool _chrIsRam;
    private readonly IMapper _mapper;

    public string Path { get; }
    public byte MapperId { get; }
    public int PrgRomBanks { get; }
    public int ChrRomBanks { get; }

    public MirroringMode Mirroring => _mapper.Mirroring;

    private Cartridge(
        string path,
        byte mapperId,
        int prgRomBanks,
        int chrRomBanks,
        byte[] prgRom,
        byte[] chrData,
        bool chrIsRam,
        IMapper mapper)
    {
        Path = path;
        MapperId = mapperId;
        PrgRomBanks = prgRomBanks;
        ChrRomBanks = chrRomBanks;
        _prgRom = prgRom;
        _chrData = chrData;
        _chrIsRam = chrIsRam;
        _mapper = mapper;
        _mapper.Reset();
    }

    public static Cartridge LoadFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("ROM path cannot be empty.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("ROM file not found.", path);
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 16)
        {
            throw new InvalidDataException("Invalid iNES file: header is incomplete.");
        }

        if (bytes[0] != 'N' || bytes[1] != 'E' || bytes[2] != 'S' || bytes[3] != 0x1A)
        {
            throw new InvalidDataException("Invalid iNES file: missing NES header magic.");
        }

        var prgRomBanks = bytes[4];
        var chrRomBanks = bytes[5];
        var flags6 = bytes[6];
        var flags7 = bytes[7];

        var mapperId = (byte)((flags7 & 0xF0) | (flags6 >> 4));
        var isNes2 = (flags7 & 0x0C) == 0x08;
        if (isNes2)
        {
            throw new NotSupportedException("NES 2.0 ROM format is not supported yet.");
        }

        var hardMirroring = (flags6 & 0x01) != 0 ? MirroringMode.Vertical : MirroringMode.Horizontal;
        var fourScreen = (flags6 & 0x08) != 0;
        var mirroring = fourScreen ? MirroringMode.FourScreen : hardMirroring;

        var offset = 16;
        var hasTrainer = (flags6 & 0x04) != 0;
        if (hasTrainer)
        {
            offset += 512;
        }

        var prgSize = prgRomBanks * 16 * 1024;
        var chrSize = chrRomBanks * 8 * 1024;

        if (bytes.Length < offset + prgSize + chrSize)
        {
            throw new InvalidDataException("Invalid iNES file: ROM data is truncated.");
        }

        var prgRom = new byte[prgSize];
        Array.Copy(bytes, offset, prgRom, 0, prgSize);

        offset += prgSize;
        var chrIsRam = chrRomBanks == 0;
        var chrData = new byte[chrIsRam ? 8 * 1024 : chrSize];
        if (!chrIsRam)
        {
            Array.Copy(bytes, offset, chrData, 0, chrSize);
        }

        var mapper = CreateMapper(mapperId, prgRomBanks, prgSize, chrData.Length, chrRomBanks == 0 ? 1 : chrRomBanks, mirroring);

        return new Cartridge(
            path: System.IO.Path.GetFullPath(path),
            mapperId: mapperId,
            prgRomBanks: prgRomBanks,
            chrRomBanks: chrRomBanks,
            prgRom: prgRom,
            chrData: chrData,
            chrIsRam: chrIsRam,
            mapper: mapper);
    }

    public bool CpuRead(ushort address, out byte data)
    {
        if (!_mapper.CpuRead(address, out var mappedAddress, out var isPrgRam))
        {
            data = 0x00;
            return false;
        }

        if (mappedAddress < 0)
        {
            data = 0x00;
            return true;
        }

        if (isPrgRam)
        {
            data = (uint)mappedAddress < _prgRam.Length ? _prgRam[mappedAddress] : (byte)0;
            return true;
        }

        data = (uint)mappedAddress < _prgRom.Length ? _prgRom[mappedAddress] : (byte)0;
        return true;
    }

    public bool CpuWrite(ushort address, byte data)
    {
        if (!_mapper.CpuWrite(address, data, out var mappedAddress, out var isPrgRam))
        {
            return false;
        }

        if (mappedAddress >= 0 && isPrgRam && (uint)mappedAddress < _prgRam.Length)
        {
            _prgRam[mappedAddress] = data;
        }

        return true;
    }

    public bool PpuRead(ushort address, out byte data)
    {
        if (!_mapper.PpuRead(address, out var mappedAddress))
        {
            data = 0;
            return false;
        }

        data = (uint)mappedAddress < _chrData.Length ? _chrData[mappedAddress] : (byte)0;
        return true;
    }

    public bool PpuWrite(ushort address, byte data)
    {
        if (!_mapper.PpuWrite(address, out var mappedAddress))
        {
            return false;
        }

        if (_chrIsRam && mappedAddress >= 0 && (uint)mappedAddress < _chrData.Length)
        {
            _chrData[mappedAddress] = data;
        }

        return true;
    }

    private static IMapper CreateMapper(
        byte mapperId,
        int prgRomBanks,
        int prgRomBytes,
        int chrDataBytes,
        int chrRomBanks,
        MirroringMode mirroring)
    {
        return mapperId switch
        {
            0 => new Mapper000(prgRomBanks, mirroring),
            1 => new Mapper001(prgRomBanks, chrRomBanks, mirroring),
            2 => new Mapper002(prgRomBanks, mirroring),
            3 => new Mapper003(prgRomBanks, chrRomBanks, mirroring),
            4 => new Mapper004(prgRomBytes, chrDataBytes, mirroring),
            _ => throw new NotSupportedException(
                $"Mapper {mapperId} is not supported yet. Supported mappers: 0, 1, 2, 3, 4.")
        };
    }
}
