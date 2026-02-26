using cunes.Cartridge;
using cunes.Cpu;
using cunes.Core;
using cunes.Ppu;

namespace cunes.Bus;

public sealed class SystemBus
{
    private readonly byte[] _cpuRam = new byte[2 * 1024];
    private readonly byte[] _controllerState = new byte[2];
    private readonly byte[] _controllerShift = new byte[2];
    private readonly Apu2A03 _apu = new();
    private bool _controllerStrobe;
    private byte _openBus;
    private readonly Cpu6502 _cpu;
    private readonly Ppu2C02 _ppu;
    private Cartridge.Cartridge? _cartridge;

    public SystemBus(Cpu6502 cpu, Ppu2C02 ppu)
    {
        _cpu = cpu;
        _ppu = ppu;

        _cpu.ConnectBus(this);
        _apu.ConnectCpuReader(ReadForDmc);
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
        _apu.Reset();
    }

    public byte Read(ushort address)
    {
        if (address == 0x4015)
        {
            var value = _apu.ReadStatus(_openBus);
            _openBus = value;
            return value;
        }

        if (_cartridge is not null && _cartridge.CpuRead(address, out var data))
        {
            _openBus = data;
            return data;
        }

        if (address is 0x4016 or 0x4017)
        {
            var index = address - 0x4016;
            var value = (byte)((_openBus & 0xFE) | (_controllerShift[index] & 0x01));
            if (!_controllerStrobe)
            {
                _controllerShift[index] >>= 1;
                _controllerShift[index] |= 0x80; // open bus behavior approximation after 8 reads
            }

            _openBus = value;
            return value;
        }

        byte readValue = address switch
        {
            <= 0x1FFF => _cpuRam[address & 0x07FF],
            <= 0x3FFF => _ppu.CpuRead((ushort)(address & 0x0007)),
            >= 0x4020 when _cartridge is null => (byte)0x00,
            _ => _openBus
        };

        _openBus = readValue;

        return readValue;
    }

    public void Write(ushort address, byte data)
    {
        _openBus = data;

        if (address is >= 0x4000 and <= 0x4013 || address == 0x4015 || address == 0x4017)
        {
            _apu.WriteRegister(address, data);
        }

        if (_cartridge is not null && _cartridge.CpuWrite(address, data))
        {
            return;
        }

        switch (address)
        {
            case <= 0x1FFF:
                _cpuRam[address & 0x07FF] = data;
                break;
            case <= 0x3FFF:
                _ppu.CpuWrite((ushort)(address & 0x0007), data);
                break;
            case 0x4014:
                DoOamDma(data);
                break;
            case 0x4016:
                _controllerStrobe = (data & 0x01) != 0;
                if (_controllerStrobe)
                {
                    _controllerShift[0] = _controllerState[0];
                    _controllerShift[1] = _controllerState[1];
                }
                break;
            case 0x4017:
                break;
        }
    }

    public void SetControllerState(int player, byte state)
    {
        if ((uint)player >= 2)
        {
            return;
        }

        _controllerState[player] = state;
        if (_controllerStrobe)
        {
            _controllerShift[player] = state;
        }
    }

    private void DoOamDma(byte page)
    {
        var baseAddress = (ushort)(page << 8);
        for (ushort i = 0; i < 256; i++)
        {
            _ppu.WriteOamByte(Read((ushort)(baseAddress + i)));
        }
    }

    public void ClockCpuCycle()
    {
        _apu.ClockCpu();
    }

    public bool ConsumeIrq()
    {
        return _apu.ConsumeIrq();
    }

    public int DrainAudioSamples(float[] destination, int maxSamples)
    {
        return _apu.DrainSamples(destination, maxSamples);
    }

    public Apu2A03.AudioChannelDebugSnapshot DrainApuAudioChannelDebugSnapshot()
    {
        return _apu.DrainAudioChannelDebugSnapshot();
    }

    public Apu2A03.NoiseDebugSnapshot DrainApuNoiseDebugSnapshot()
    {
        return _apu.DrainNoiseDebugSnapshot();
    }

    public Apu2A03.MixDebugSnapshot DrainApuMixDebugSnapshot()
    {
        return _apu.DrainMixDebugSnapshot();
    }

    private byte ReadForDmc(ushort address)
    {
        if (_cartridge is not null && _cartridge.CpuRead(address, out var cartData))
        {
            return cartData;
        }

        if (address <= 0x1FFF)
        {
            return _cpuRam[address & 0x07FF];
        }

        return 0x00;
    }
}
