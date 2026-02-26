using cunes.Cartridge;

namespace cunes.Ppu;

public sealed class Ppu2C02
{
    public const int ScreenWidth = 256;
    public const int ScreenHeight = 240;
    // Keep decay well below 1 second; tests only require decaying before that.
    private const int OpenBusDecayPpuCycles = 1_000_000;
    private static readonly byte[] SystemPalette =
    {
        84,84,84, 0,30,116, 8,16,144, 48,0,136, 68,0,100, 92,0,48, 84,4,0, 60,24,0,
        32,42,0, 8,58,0, 0,64,0, 0,60,0, 0,50,60, 0,0,0, 0,0,0, 0,0,0,
        152,150,152, 8,76,196, 48,50,236, 92,30,228, 136,20,176, 160,20,100, 152,34,32, 120,60,0,
        84,90,0, 40,114,0, 8,124,0, 0,118,40, 0,102,120, 0,0,0, 0,0,0, 0,0,0,
        236,238,236, 76,154,236, 120,124,236, 176,98,236, 228,84,236, 236,88,180, 236,106,100, 212,136,32,
        160,170,0, 116,196,0, 76,208,32, 56,204,108, 56,180,204, 60,60,60, 0,0,0, 0,0,0,
        236,238,236, 168,204,236, 188,188,236, 212,178,236, 236,174,236, 236,174,212, 236,180,176, 228,196,144,
        204,210,120, 180,222,120, 168,226,144, 152,226,180, 160,214,228, 160,162,160, 0,0,0, 0,0,0
    };

    private readonly byte[] _nameTable = new byte[0x1000];
    private readonly byte[] _paletteTable = new byte[0x20];
    private readonly byte[] _oam = new byte[256];
    private readonly byte[] _backgroundOpaque = new byte[ScreenWidth * ScreenHeight];
    private readonly int[] _activeSpriteIndices = new int[8];

    private Cartridge.Cartridge? _cartridge;

    private byte _control;
    private byte _mask;
    private byte _status;
    private byte _oamAddress;

    private byte _fineX;
    private bool _addressLatch;
    private byte _ppuDataBuffer;
    private byte _ppuOpenBus;
    private int _ppuOpenBusAgeCycles;
    private ushort _vramAddress;
    private ushort _tempVramAddress;
    private ushort _scanlineScrollVramAddress;
    private byte _scanlineFineX;

    private int _scanline;
    private int _dot;
    private bool _nmiRequested;
    private bool _resetFlagActive;
    private int _activeSpriteCount;
    private int _preparedSpriteScanline = -1;

    public byte[] FrameBuffer { get; } = new byte[ScreenWidth * ScreenHeight * 4];

    public ulong Cycles { get; private set; }
    public ulong FrameCount { get; private set; }

    public bool ConsumeNmi()
    {
        if (!_nmiRequested)
        {
            return false;
        }

        _nmiRequested = false;
        return true;
    }

    public void ConnectCartridge(Cartridge.Cartridge cartridge)
    {
        _cartridge = cartridge;
    }

    public void DisconnectCartridge()
    {
        _cartridge = null;
    }

    public void Reset()
    {
        Array.Clear(FrameBuffer);
        Array.Clear(_oam);
        _control = 0;
        _mask = 0;
        _status = 0;
        _oamAddress = 0;
        _fineX = 0;
        _addressLatch = false;
        _ppuDataBuffer = 0;
        _ppuOpenBus = 0;
        _ppuOpenBusAgeCycles = OpenBusDecayPpuCycles;
        _vramAddress = 0;
        _tempVramAddress = 0;
        _scanlineScrollVramAddress = 0;
        _scanlineFineX = 0;
        _scanline = 0;
        _dot = 0;
        _nmiRequested = false;
        _resetFlagActive = true;
        _activeSpriteCount = 0;
        _preparedSpriteScanline = -1;
        Cycles = 0;
        FrameCount = 0;
    }

    public void Clock()
    {
        Cycles++;
        if (_ppuOpenBusAgeCycles < OpenBusDecayPpuCycles)
        {
            _ppuOpenBusAgeCycles++;
            if (_ppuOpenBusAgeCycles >= OpenBusDecayPpuCycles)
            {
                _ppuOpenBus = 0;
            }
        }

        if (_dot == 0)
        {
            _preparedSpriteScanline = -1;
        }

        if (_scanline is >= 0 and < 240 && _dot == 1)
        {
            // Keep scroll stable for the current scanline to avoid per-pixel tearing.
            _scanlineScrollVramAddress = _tempVramAddress;
            _scanlineFineX = _fineX;
            PrepareActiveSpritesForScanline(_scanline);
        }

        if (_scanline is >= 0 and < 240 && _dot is >= 1 and <= 256)
        {
            RenderCurrentPixel(_dot - 1, _scanline);
        }

        _dot++;
        if (_dot >= 341)
        {
            _dot = 0;
            _scanline++;
            if (_scanline >= 262)
            {
                _scanline = 0;
                FrameCount++;
            }
        }

        if (_scanline == 241 && _dot == 1)
        {
            _status |= 0x80;
            if ((_control & 0x80) != 0)
            {
                _nmiRequested = true;
            }
        }

        if (_scanline == 261 && _dot == 1)
        {
            _status &= 0x1F;
            _resetFlagActive = false;
        }
    }

    public byte CpuRead(ushort registerAddress)
    {
        var value = registerAddress switch
        {
            0x0002 => ReadStatus(),
            0x0004 => ReadOamData(),
            0x0007 => ReadPpuData(),
            _ => _ppuOpenBus
        };

        LatchOpenBus(value);
        return value;
    }

    public void CpuWrite(ushort registerAddress, byte data)
    {
        if (_resetFlagActive && (registerAddress is 0x0000 or 0x0001 or 0x0005 or 0x0006))
        {
            LatchOpenBus(data);
            return;
        }

        switch (registerAddress)
        {
            case 0x0000:
                _control = data;
                _tempVramAddress = (ushort)((_tempVramAddress & 0xF3FF) | ((data & 0x03) << 10));
                break;
            case 0x0001:
                _mask = data;
                break;
            case 0x0003:
                _oamAddress = data;
                break;
            case 0x0004:
                _oam[_oamAddress] = data;
                _oamAddress++;
                break;
            case 0x0005:
                if (!_addressLatch)
                {
                    _fineX = (byte)(data & 0x07);
                    _tempVramAddress = (ushort)((_tempVramAddress & 0xFFE0) | (data >> 3));
                    _addressLatch = true;
                }
                else
                {
                    _tempVramAddress = (ushort)((_tempVramAddress & 0x8FFF) | ((data & 0x07) << 12));
                    _tempVramAddress = (ushort)((_tempVramAddress & 0xFC1F) | ((data & 0xF8) << 2));
                    _addressLatch = false;
                }

                break;
            case 0x0006:
                if (!_addressLatch)
                {
                    _tempVramAddress = (ushort)((_tempVramAddress & 0x00FF) | ((data & 0x3F) << 8));
                    _addressLatch = true;
                }
                else
                {
                    _tempVramAddress = (ushort)((_tempVramAddress & 0xFF00) | data);
                    _vramAddress = _tempVramAddress;
                    _addressLatch = false;
                }

                break;
            case 0x0007:
                WritePpuMemory(_vramAddress, data);
                IncrementVramAddress();
                break;
        }

        LatchOpenBus(data);
    }

    public void WriteOamByte(byte value)
    {
        _oam[_oamAddress] = value;
        _oamAddress++;
    }

    private byte ReadStatus()
    {
        var data = (byte)((_status & 0xE0) | (_ppuOpenBus & 0x1F));
        _status &= 0x7F;
        _addressLatch = false;
        return data;
    }

    private byte ReadOamData()
    {
        return _oam[_oamAddress];
    }

    private byte ReadPpuData()
    {
        var value = ReadPpuMemory(_vramAddress);
        byte data;

        if (_vramAddress < 0x3F00)
        {
            data = _ppuDataBuffer;
            _ppuDataBuffer = value;
        }
        else
        {
            // Palette RAM is 6-bit; top 2 bits come from PPU open bus.
            data = (byte)((value & 0x3F) | (_ppuOpenBus & 0xC0));
            _ppuDataBuffer = ReadPpuMemory((ushort)(_vramAddress - 0x1000));
        }

        IncrementVramAddress();
        LatchOpenBus(data);
        return data;
    }

    private void IncrementVramAddress()
    {
        var increment = (_control & 0x04) != 0 ? 32 : 1;
        _vramAddress = (ushort)((_vramAddress + increment) & 0x3FFF);
    }

    private byte ReadPpuMemory(ushort address)
    {
        address &= 0x3FFF;

        if (address <= 0x1FFF)
        {
            if (_cartridge is not null && _cartridge.PpuRead(address, out var chrData))
            {
                return chrData;
            }

            return 0;
        }

        if (address <= 0x3EFF)
        {
            var mirroredAddress = MapNametableAddress(address);
            return _nameTable[mirroredAddress];
        }

        var paletteAddress = NormalizePaletteAddress(address);
        return _paletteTable[paletteAddress];
    }

    private void WritePpuMemory(ushort address, byte data)
    {
        address &= 0x3FFF;

        if (address <= 0x1FFF)
        {
            if (_cartridge is not null)
            {
                _cartridge.PpuWrite(address, data);
            }

            return;
        }

        if (address <= 0x3EFF)
        {
            var mirroredAddress = MapNametableAddress(address);
            _nameTable[mirroredAddress] = data;
            return;
        }

        var paletteAddress = NormalizePaletteAddress(address);
        _paletteTable[paletteAddress] = (byte)(data & 0x3F);
    }

    private int MapNametableAddress(ushort address)
    {
        var offset = (address - 0x2000) & 0x0FFF;
        var table = offset / 0x400;
        var index = offset & 0x03FF;

        var mirroring = _cartridge?.Mirroring ?? MirroringMode.Horizontal;

        return mirroring switch
        {
            MirroringMode.Vertical => (table is 0 or 2 ? 0 : 1) * 0x400 + index,
            MirroringMode.Horizontal => (table is 0 or 1 ? 0 : 1) * 0x400 + index,
            MirroringMode.OneScreenLower => index,
            MirroringMode.OneScreenUpper => 0x400 + index,
            MirroringMode.FourScreen => table * 0x400 + index,
            _ => (table is 0 or 1 ? 0 : 1) * 0x400 + index
        };
    }

    private static int NormalizePaletteAddress(ushort address)
    {
        var paletteAddress = (address - 0x3F00) & 0x1F;
        if (paletteAddress is 0x10 or 0x14 or 0x18 or 0x1C)
        {
            paletteAddress -= 0x10;
        }

        return paletteAddress;
    }

    private void RenderCurrentPixel(int x, int y)
    {
        var backgroundEnabled = (_mask & 0x08) != 0;
        var spritesEnabled = (_mask & 0x10) != 0;
        var showBgLeft = (_mask & 0x02) != 0;
        var showSpritesLeft = (_mask & 0x04) != 0;

        var bgPipelineEnabled = backgroundEnabled || spritesEnabled;
        var bgOpaque = false;
        var bgColor = ReadPaletteMemory(0x3F00);
        if (bgPipelineEnabled)
        {
            bgColor = GetBackgroundPixelColor(x, y, out bgOpaque);
        }

        var bgVisible = backgroundEnabled && (x >= 8 || showBgLeft);
        var spritesVisible = spritesEnabled && (x >= 8 || showSpritesLeft);
        var bgHitVisible = backgroundEnabled && spritesEnabled && (x >= 8 || (showBgLeft && showSpritesLeft));

        _backgroundOpaque[y * ScreenWidth + x] = (byte)((bgVisible && bgOpaque) ? 1 : 0);

        var finalColor = bgVisible ? bgColor : ReadPaletteMemory(0x3F00);
        if (spritesVisible)
        {
            var hasSprite = TryGetSpritePixel(x, y, out var spriteColor, out var behindBackground, out var isSpriteZero);
            if (hasSprite)
            {
                if (isSpriteZero && bgHitVisible && bgOpaque && x < 255)
                {
                    _status |= 0x40;
                }

                if (!behindBackground || !bgVisible || !bgOpaque)
                {
                    finalColor = spriteColor;
                }
            }
        }

        WritePixel(x, y, finalColor);
    }

    private byte GetBackgroundPixelColor(int x, int y, out bool opaque)
    {
        var patternBase = (_control & 0x10) != 0 ? 0x1000 : 0x0000;
        var baseNametable = (_scanlineScrollVramAddress >> 10) & 0x03;
        var baseNtX = baseNametable & 0x01;
        var baseNtY = (baseNametable >> 1) & 0x01;
        var scrollX = ((_scanlineScrollVramAddress & 0x001F) << 3) + _scanlineFineX;
        var scrollY = (((_scanlineScrollVramAddress >> 5) & 0x001F) << 3) + ((_scanlineScrollVramAddress >> 12) & 0x0007);

        var worldX = scrollX + x;
        var worldY = scrollY + y;

        var ntOffsetX = (worldX / 256) & 0x01;
        var ntOffsetY = (worldY / 240) & 0x01;

        var logicalNtX = (baseNtX + ntOffsetX) & 0x01;
        var logicalNtY = (baseNtY + ntOffsetY) & 0x01;
        var nametableSelect = logicalNtX | (logicalNtY << 1);

        var localX = worldX % 256;
        var localY = worldY % 240;

        var tileX = localX / 8;
        var tileY = localY / 8;
        var fineX = localX & 0x07;
        var fineY = localY & 0x07;

        var nameTableBase = 0x2000 + nametableSelect * 0x400;
        var tileIndex = ReadPpuMemory((ushort)(nameTableBase + tileY * 32 + tileX));

        var attributeBase = nameTableBase + 0x03C0;
        var attributeByte = ReadPpuMemory((ushort)(attributeBase + (tileY / 4) * 8 + (tileX / 4)));
        var quadrant = ((tileY & 0x02) != 0 ? 2 : 0) | ((tileX & 0x02) != 0 ? 1 : 0);
        var paletteId = quadrant switch
        {
            0 => attributeByte & 0x03,
            1 => (attributeByte >> 2) & 0x03,
            2 => (attributeByte >> 4) & 0x03,
            _ => (attributeByte >> 6) & 0x03
        };

        var tileAddress = patternBase + tileIndex * 16;
        var plane0 = ReadPpuMemory((ushort)(tileAddress + fineY));
        var plane1 = ReadPpuMemory((ushort)(tileAddress + fineY + 8));
        var shift = 7 - fineX;
        var lo = (plane0 >> shift) & 0x01;
        var hi = (plane1 >> shift) & 0x01;
        var colorIndex = (hi << 1) | lo;

        opaque = colorIndex != 0;
        return ResolveBackgroundColor(colorIndex, paletteId);
    }

    private bool TryGetSpritePixel(int x, int y, out byte spriteColor, out bool behindBackground, out bool isSpriteZero)
    {
        spriteColor = 0;
        behindBackground = false;
        isSpriteZero = false;

        var spriteHeight = (_control & 0x20) != 0 ? 16 : 8;
        var spritePatternBase = (_control & 0x08) != 0 ? 0x1000 : 0x0000;

        for (var i = 0; i < _activeSpriteCount; i++)
        {
            var spriteIndex = _activeSpriteIndices[i];
            var oamBase = spriteIndex * 4;
            var spriteY = _oam[oamBase] + 1;
            var tile = _oam[oamBase + 1];
            var attributes = _oam[oamBase + 2];
            var spriteX = _oam[oamBase + 3];

            if (y < spriteY || y >= spriteY + spriteHeight || x < spriteX || x >= spriteX + 8)
            {
                continue;
            }

            var flipHorizontal = (attributes & 0x40) != 0;
            var flipVertical = (attributes & 0x80) != 0;
            var paletteId = attributes & 0x03;

            var row = y - spriteY;
            var sourceRow = flipVertical ? (spriteHeight - 1 - row) : row;

            int tileAddress;
            if (spriteHeight == 16)
            {
                var tableBase = (tile & 0x01) != 0 ? 0x1000 : 0x0000;
                var tileNumber = tile & 0xFE;
                if (sourceRow >= 8)
                {
                    tileNumber++;
                }

                tileAddress = tableBase + tileNumber * 16 + (sourceRow & 0x07);
            }
            else
            {
                tileAddress = spritePatternBase + tile * 16 + sourceRow;
            }

            var plane0 = ReadPpuMemory((ushort)tileAddress);
            var plane1 = ReadPpuMemory((ushort)(tileAddress + 8));

            var col = x - spriteX;
            var sourceCol = flipHorizontal ? col : (7 - col);
            var lo = (plane0 >> sourceCol) & 0x01;
            var hi = (plane1 >> sourceCol) & 0x01;
            var colorIndex = (hi << 1) | lo;
            if (colorIndex == 0)
            {
                continue;
            }

            spriteColor = ResolveSpriteColor(colorIndex, paletteId);
            behindBackground = (attributes & 0x20) != 0;
            isSpriteZero = spriteIndex == 0;
            return true;
        }

        return false;
    }

    private void PrepareActiveSpritesForScanline(int scanline)
    {
        if (_preparedSpriteScanline == scanline)
        {
            return;
        }

        _preparedSpriteScanline = scanline;
        _activeSpriteCount = 0;

        var spriteHeight = (_control & 0x20) != 0 ? 16 : 8;
        if (EvaluateSpriteOverflowForScanline(scanline, spriteHeight))
        {
            _status |= 0x20;
        }

        for (var spriteIndex = 0; spriteIndex < 64; spriteIndex++)
        {
            var spriteY = _oam[spriteIndex * 4] + 1;
            if (scanline < spriteY || scanline >= spriteY + spriteHeight)
            {
                continue;
            }

            _activeSpriteIndices[_activeSpriteCount++] = spriteIndex;
            if (_activeSpriteCount >= 8)
            {
                break;
            }
        }
    }

    private bool EvaluateSpriteOverflowForScanline(int scanline, int spriteHeight)
    {
        // Emulate the 2C02 sprite-overflow bug by tracking OAM address progression
        // after secondary OAM becomes full. This reproduces diagonal checks and the
        // key m==3 out-of-range case where address advances by +5.
        var inRangeCount = 0;
        var oamAddress = 0;
        while ((oamAddress >> 2) < 64)
        {
            var y = _oam[oamAddress & 0xFF] + 1;
            var inRange = scanline >= y && scanline < y + spriteHeight;

            if (inRangeCount < 8)
            {
                if (inRange)
                {
                    inRangeCount++;
                }

                oamAddress += 4;
                continue;
            }

            if (inRange)
            {
                return true;
            }

            var m = oamAddress & 0x03;
            oamAddress += m == 3 ? 5 : 1;
        }

        return false;
    }

    private byte ResolveBackgroundColor(int colorIndex, int paletteId)
    {
        if (colorIndex == 0)
        {
            return ReadPaletteMemory(0x3F00);
        }

        return ReadPaletteMemory((ushort)(0x3F00 + paletteId * 4 + colorIndex));
    }

    private byte ResolveSpriteColor(int colorIndex, int paletteId)
    {
        return ReadPaletteMemory((ushort)(0x3F10 + paletteId * 4 + colorIndex));
    }

    private byte ReadPaletteMemory(ushort address)
    {
        return _paletteTable[NormalizePaletteAddress(address)];
    }

    private void WritePixel(int x, int y, byte paletteEntry)
    {
        var idx = (paletteEntry & 0x3F) * 3;
        var fb = (y * ScreenWidth + x) * 4;
        FrameBuffer[fb + 0] = SystemPalette[idx + 0];
        FrameBuffer[fb + 1] = SystemPalette[idx + 1];
        FrameBuffer[fb + 2] = SystemPalette[idx + 2];
        FrameBuffer[fb + 3] = 255;
    }

    private void LatchOpenBus(byte value)
    {
        _ppuOpenBus = value;
        _ppuOpenBusAgeCycles = 0;
    }
}
