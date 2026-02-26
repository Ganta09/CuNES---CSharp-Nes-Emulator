using cunes.Frontend;
using NesCartridge = cunes.Cartridge.Cartridge;
using System.Diagnostics;
using System.IO;

namespace cunes.Core;

public sealed class NesApp
{
    private const byte TestRunning = 0x80;
    private const byte TestNeedsReset = 0x81;

    private readonly NesConfig _config;
    private readonly NesConsole _console;
    private readonly string? _romPath;
    private readonly bool _romTestMode;
    private readonly Stopwatch _timingStopwatch = new();
    private readonly float[] _audioBuffer = new float[4096];
    private double _lastTimestampSeconds;
    private double _cycleAccumulator;
    private double _lastApuDebugTimestampSeconds;
    private double _lastNoiseDebugTimestampSeconds;
    private double _lastMixDebugTimestampSeconds;

    public NesApp(NesConfig config, string? romPath = null, bool romTestMode = false)
    {
        _config = config;
        _romPath = romPath;
        _romTestMode = romTestMode;
        _console = new NesConsole();
    }

    public void Run()
    {
        if (!string.IsNullOrWhiteSpace(_romPath))
        {
            LoadRom(_romPath);
        }

        _console.Reset();

        if (_romTestMode)
        {
            RunRomTest();
            return;
        }

        using var renderer = CreateRenderer();
        RunInteractive(renderer);
    }

    private IRenderer CreateRenderer()
    {
        if (_config.EnableWindow)
        {
            var renderer = RaylibRenderer.TryCreate(
                _config.WindowScale,
                _config.TargetFps,
                _config.WindowTitle,
                _config.EnableAudioDebug);
            if (renderer is not null)
            {
                return renderer;
            }
        }

        return new NullRenderer();
    }

    private void RunInteractive(IRenderer renderer)
    {
        Console.WriteLine($"CPU: {_config.CpuFrequencyHz} Hz | Target: {_config.TargetFps} FPS");
        renderer.SetRomLoaded(_console.Cartridge is not null);

        var cyclesPerFrame = _config.CpuFrequencyHz / _config.TargetFps;
        var frame = 0;
        var lastRenderedPpuFrame = _console.Ppu.FrameCount;

        while (!renderer.ShouldClose)
        {
            if (ProcessUiActions(renderer))
            {
                break;
            }

            _console.Bus.SetControllerState(0, renderer.GetControllerState(0));
            _console.Bus.SetControllerState(1, renderer.GetControllerState(1));

            if (!renderer.IsInteractive)
            {
                for (var cycle = 0; cycle < cyclesPerFrame; cycle++)
                {
                    _console.Clock();
                }

                FlushAudio(renderer);
                renderer.DrawFrame(_console.Ppu.FrameBuffer);
                frame++;
                Console.WriteLine($"Frame {frame} completed");
                if (frame >= 3)
                {
                    break;
                }

                continue;
            }

            if (!_timingStopwatch.IsRunning)
            {
                _timingStopwatch.Restart();
                _lastTimestampSeconds = _timingStopwatch.Elapsed.TotalSeconds;
                _cycleAccumulator = 0.0;
            }

            var nowSeconds = _timingStopwatch.Elapsed.TotalSeconds;
            var deltaSeconds = nowSeconds - _lastTimestampSeconds;
            _lastTimestampSeconds = nowSeconds;

            if (deltaSeconds < 0)
            {
                deltaSeconds = 0;
            }
            else if (deltaSeconds > 0.25)
            {
                deltaSeconds = 0.25;
            }

            _cycleAccumulator += deltaSeconds * _config.CpuFrequencyHz;
            var maxCyclesThisFrame = cyclesPerFrame * 3;
            var cycleBudget = (int)Math.Min(_cycleAccumulator, maxCyclesThisFrame);
            var cyclesRun = 0;
            var targetPpuFrame = lastRenderedPpuFrame + 1;
            while (cyclesRun < cycleBudget)
            {
                _console.Clock();
                cyclesRun++;
                if (_console.Ppu.FrameCount >= targetPpuFrame)
                {
                    break;
                }
            }

            _cycleAccumulator -= cyclesRun;

            FlushAudio(renderer);
            MaybeLogApuAudioDebug(renderer);
            MaybeLogNoiseDebug(renderer);
            MaybeLogMixDebug(renderer);
            if (_console.Ppu.FrameCount != lastRenderedPpuFrame)
            {
                renderer.DrawFrame(_console.Ppu.FrameBuffer);
                lastRenderedPpuFrame = _console.Ppu.FrameCount;
                frame++;
            }
        }
    }

    private bool ProcessUiActions(IRenderer renderer)
    {
        while (renderer.TryDequeueUiAction(out var action))
        {
            switch (action.Type)
            {
                case UiActionType.LoadRom:
                    if (!string.IsNullOrWhiteSpace(action.RomPath))
                    {
                        try
                        {
                            LoadRom(action.RomPath);
                            renderer.SetRomLoaded(true);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to load ROM: {ex.Message}");
                        }
                    }
                    break;
                case UiActionType.CloseRom:
                    CloseRom();
                    renderer.SetRomLoaded(false);
                    break;
                case UiActionType.Exit:
                    return true;
            }
        }

        return false;
    }

    private void LoadRom(string romPath)
    {
        var cartridge = NesCartridge.LoadFromFile(romPath);
        _console.InsertCartridge(cartridge);
        _console.Reset();
        Console.WriteLine($"ROM loaded: {cartridge.Path} | Mapper {cartridge.MapperId} | PRG {cartridge.PrgRomBanks}x16KB | CHR {cartridge.ChrRomBanks}x8KB");
    }

    private void CloseRom()
    {
        if (_console.Cartridge is null)
        {
            return;
        }

        var closedPath = _console.Cartridge.Path;
        _console.RemoveCartridge();
        _console.Reset();
        Console.WriteLine($"ROM closed: {closedPath}");
    }

    private void FlushAudio(IRenderer renderer)
    {
        var available = _console.DrainAudioSamples(_audioBuffer, _audioBuffer.Length);
        if (available > 0)
        {
            renderer.SubmitAudioSamples(_audioBuffer, available);
        }
    }

    private void MaybeLogApuAudioDebug(IRenderer renderer)
    {
        if (!_config.EnableAudioDebug || !renderer.IsInteractive || !_timingStopwatch.IsRunning)
        {
            return;
        }

        var nowSeconds = _timingStopwatch.Elapsed.TotalSeconds;
        if (nowSeconds - _lastApuDebugTimestampSeconds < 1.0)
        {
            return;
        }

        _lastApuDebugTimestampSeconds = nowSeconds;
        var s = _console.DrainApuAudioChannelDebugSnapshot();
        if (s.Samples <= 0)
        {
            return;
        }

        var inv = 1.0 / s.Samples;
        var pulseDuty = 100.0 * s.PulseNonZero * inv;
        var triDuty = 100.0 * s.TriangleNonZero * inv;
        var noiseDuty = 100.0 * s.NoiseNonZero * inv;
        var dmcDuty = 100.0 * s.DmcNonZero * inv;
        var pulseAvg = s.PulseSum * inv;
        var triAvg = s.TriangleSum * inv;
        var noiseAvg = s.NoiseSum * inv;
        var dmcAvg = s.DmcSum * inv;

        Console.WriteLine(
            $"[apu] n={s.Samples}, pulse(duty={pulseDuty:F1}% avg={pulseAvg:F2}), " +
            $"tri(duty={triDuty:F1}% avg={triAvg:F2}), noise(duty={noiseDuty:F1}% avg={noiseAvg:F2}), " +
            $"dmc(duty={dmcDuty:F1}% avg={dmcAvg:F2})");
    }

    private void MaybeLogNoiseDebug(IRenderer renderer)
    {
        if (!_config.EnableNoiseDebug || !renderer.IsInteractive || !_timingStopwatch.IsRunning)
        {
            return;
        }

        var nowSeconds = _timingStopwatch.Elapsed.TotalSeconds;
        if (nowSeconds - _lastNoiseDebugTimestampSeconds < 1.0)
        {
            return;
        }

        _lastNoiseDebugTimestampSeconds = nowSeconds;
        var n = _console.DrainApuNoiseDebugSnapshot();

        Console.WriteLine(
            $"[noise] mode={(n.ModeShort ? "short" : "long")}, periodIdx={n.PeriodIndex}, period={n.PeriodValue}, timer={n.TimerCounter}, " +
            $"len={n.LengthCounter}, envDecay={n.EnvelopeDecay}, constVol={n.ConstantVolume}, envPer={n.EnvelopePeriod}, " +
            $"lfsrReloads={n.TimerReloads}, shortReloads={n.ShortModeReloads}, " +
            $"regWrites={n.PeriodWrites}, modeChg={n.ModeChanges}, periodChg={n.PeriodIndexChanges}");
    }

    private void MaybeLogMixDebug(IRenderer renderer)
    {
        if (!_config.EnableMixDebug || !renderer.IsInteractive || !_timingStopwatch.IsRunning)
        {
            return;
        }

        var nowSeconds = _timingStopwatch.Elapsed.TotalSeconds;
        if (nowSeconds - _lastMixDebugTimestampSeconds < 1.0)
        {
            return;
        }

        _lastMixDebugTimestampSeconds = nowSeconds;
        var m = _console.DrainApuMixDebugSnapshot();
        if (m.Samples <= 0)
        {
            return;
        }

        var inv = 1.0 / m.Samples;
        var pulseAvg = m.PulseOutSum * inv;
        var triAvg = m.TriangleOutSum * inv;
        var noiseAvg = m.NoiseOutSum * inv;
        var dmcAvg = m.DmcOutSum * inv;
        var preAvg = m.PreFilterSum * inv;
        var postAvg = m.PostFilterSum * inv;

        var eps = 1e-9;
        var total = Math.Abs(pulseAvg) + Math.Abs(triAvg) + Math.Abs(noiseAvg) + Math.Abs(dmcAvg) + eps;
        var pulsePct = 100.0 * Math.Abs(pulseAvg) / total;
        var triPct = 100.0 * Math.Abs(triAvg) / total;
        var noisePct = 100.0 * Math.Abs(noiseAvg) / total;
        var dmcPct = 100.0 * Math.Abs(dmcAvg) / total;

        Console.WriteLine(
            $"[mix] pulse={pulsePct:F1}% tri={triPct:F1}% noise={noisePct:F1}% dmc={dmcPct:F1}% | " +
            $"avg pre={preAvg:F4} post={postAvg:F4} | " +
            $"abs pulse={Math.Abs(pulseAvg):F4} tri={Math.Abs(triAvg):F4} noise={Math.Abs(noiseAvg):F4} dmc={Math.Abs(dmcAvg):F4}");
    }

    private void RunRomTest()
    {
        if (!string.IsNullOrWhiteSpace(_romPath) &&
            Path.GetFileName(_romPath).Equals("nestest.nes", StringComparison.OrdinalIgnoreCase))
        {
            RunNestestAutomation();
            return;
        }

        Console.WriteLine("ROM test mode enabled (monitoring $6000 status protocol).");

        var maxCpuCycles = 150_000_000UL;
        var pollInterval = 1000UL;
        var resetDelayCycles = (ulong)(_config.CpuFrequencyHz / 10.0); // 100 ms

        var resetRequestedAt = (ulong?)null;
        var resetDone = false;

        for (ulong cycle = 0; cycle < maxCpuCycles; cycle++)
        {
            _console.Cpu.Clock();

            if (cycle % pollInterval != 0)
            {
                continue;
            }

            if (!HasTestSignature())
            {
                continue;
            }

            var status = _console.Bus.Read(0x6000);
            if (status == TestRunning)
            {
                continue;
            }

            if (status == TestNeedsReset)
            {
                if (!resetDone)
                {
                    resetRequestedAt ??= cycle;
                    if (cycle - resetRequestedAt.Value >= resetDelayCycles)
                    {
                        _console.Reset();
                        resetDone = true;
                        Console.WriteLine("ROM requested delayed reset, reset applied.");
                    }
                }

                continue;
            }

            var message = ReadTestMessage();
            var code = status;

            if (!string.IsNullOrWhiteSpace(message))
            {
                Console.WriteLine(message);
            }

            if (code == 0)
            {
                Console.WriteLine("ROM TEST PASS (code 0).");
                Environment.ExitCode = 0;
            }
            else
            {
                Console.WriteLine($"ROM TEST FAIL (code {code}).");
                Environment.ExitCode = code;
            }

            return;
        }

        throw new TimeoutException("ROM test timeout: no terminal status detected at $6000.");
    }

    private void RunNestestAutomation()
    {
        Console.WriteLine("ROM test mode enabled (nestest automation: PC=$C000, result at $0002/$0003).");

        _console.Cpu.SetProgramCounter(0xC000);

        const ulong maxCpuCycles = 80_000_000;
        const ulong pollInterval = 1000;

        for (ulong cycle = 0; cycle < maxCpuCycles; cycle++)
        {
            _console.Cpu.Clock();

            if (cycle % pollInterval != 0)
            {
                continue;
            }

            var resultLo = _console.Bus.Read(0x0002);
            var resultHi = _console.Bus.Read(0x0003);

            if (_console.Cpu.ProgramCounter == 0xC66E)
            {
                if (resultLo == 0 && resultHi == 0)
                {
                    Console.WriteLine("NESTEST PASS ($0002=$00, $0003=$00).");
                    Environment.ExitCode = 0;
                }
                else
                {
                    Console.WriteLine($"NESTEST FAIL ($0002=${resultLo:X2}, $0003=${resultHi:X2}).");
                    Environment.ExitCode = resultLo != 0 ? resultLo : resultHi;
                }

                return;
            }

            if (resultLo != 0 || resultHi != 0)
            {
                Console.WriteLine($"NESTEST FAIL ($0002=${resultLo:X2}, $0003=${resultHi:X2}).");
                Environment.ExitCode = resultLo != 0 ? resultLo : resultHi;
                return;
            }
        }

        throw new TimeoutException("nestest timeout: no terminal condition detected.");
    }

    private bool HasTestSignature()
    {
        return _console.Bus.Read(0x6001) == 0xDE
            && _console.Bus.Read(0x6002) == 0xB0
            && _console.Bus.Read(0x6003) == 0x61;
    }

    private string ReadTestMessage()
    {
        const int maxLen = 4096;
        var chars = new List<char>(256);

        for (ushort i = 0; i < maxLen; i++)
        {
            var b = _console.Bus.Read((ushort)(0x6004 + i));
            if (b == 0x00)
            {
                break;
            }

            chars.Add(b >= 32 && b <= 126 ? (char)b : '.');
        }

        return new string(chars.ToArray());
    }
}
