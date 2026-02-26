using cunes.Bus;
using cunes.Cartridge;
using cunes.Cpu;
using cunes.Ppu;

namespace cunes.Core;

public sealed class NesConsole
{
    public Cpu6502 Cpu { get; }
    public Ppu2C02 Ppu { get; }
    public SystemBus Bus { get; }
    public Cartridge.Cartridge? Cartridge { get; private set; }

    public NesConsole()
    {
        Cpu = new Cpu6502();
        Ppu = new Ppu2C02();
        Bus = new SystemBus(Cpu, Ppu);
    }

    public void InsertCartridge(Cartridge.Cartridge cartridge)
    {
        Cartridge = cartridge;
        Bus.ConnectCartridge(cartridge);
        Ppu.ConnectCartridge(cartridge);
    }

    public void RemoveCartridge()
    {
        Cartridge = null;
        Bus.DisconnectCartridge();
        Ppu.DisconnectCartridge();
    }

    public void Reset()
    {
        Bus.Reset();
        Cpu.Reset();
        Ppu.Reset();
    }

    public void Clock()
    {
        ClockPpuAndDispatchNmi();
        ClockPpuAndDispatchNmi();
        ClockPpuAndDispatchNmi();

        if (Bus.ConsumeDmcCpuStallCycle())
        {
            Cpu.HaltCycle();
        }
        else
        {
            Cpu.Clock();
        }
        Bus.ClockCpuCycle();

        if (Bus.ConsumeIrq())
        {
            Cpu.Irq();
        }
    }

    private void ClockPpuAndDispatchNmi()
    {
        Ppu.Clock();
        if (Ppu.ConsumeNmi())
        {
            Cpu.Nmi();
        }
    }

    public int DrainAudioSamples(float[] destination, int maxSamples)
    {
        return Bus.DrainAudioSamples(destination, maxSamples);
    }

    public Apu2A03.AudioChannelDebugSnapshot DrainApuAudioChannelDebugSnapshot()
    {
        return Bus.DrainApuAudioChannelDebugSnapshot();
    }

    public Apu2A03.NoiseDebugSnapshot DrainApuNoiseDebugSnapshot()
    {
        return Bus.DrainApuNoiseDebugSnapshot();
    }

    public Apu2A03.MixDebugSnapshot DrainApuMixDebugSnapshot()
    {
        return Bus.DrainApuMixDebugSnapshot();
    }
}
