namespace cunes.Core;

public sealed class Apu2A03
{
    private const double CpuFrequency = 1_789_773.0;
    private const int SampleRate = 44_100;
    private const int MaxQueuedSamples = SampleRate / 4;
    private static readonly byte[] LengthTable =
    {
        10, 254, 20, 2, 40, 4, 80, 6,
        160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22,
        192, 24, 72, 26, 16, 28, 32, 30
    };

    private readonly Queue<float> _sampleQueue = new(MaxQueuedSamples);
    private readonly PulseChannel _pulse1 = new(true);
    private readonly PulseChannel _pulse2 = new(false);
    private readonly TriangleChannel _triangle = new();
    private readonly NoiseChannel _noise = new();
    private readonly DmcChannel _dmc = new();

    private int _frameCounterCycles;
    private bool _fiveStepMode;
    private bool _frameIrqInhibit;
    private bool _frameIrqPending;
    private bool _apuCycleOdd;
    private bool _lastApuCycleWasPut;
    private bool _frameIrqClearPendingOnPutToGet;
    private double _sampleAccumulator;
    private float _hpPrevInput;
    private float _hpPrevOutput;
    private float _lpOutput;
    private Func<ushort, byte>? _cpuRead;
    private long _debugSampleCount;
    private long _debugPulseNonZero;
    private long _debugTriangleNonZero;
    private long _debugNoiseNonZero;
    private long _debugDmcNonZero;
    private double _debugPulseSum;
    private double _debugTriangleSum;
    private double _debugNoiseSum;
    private double _debugDmcSum;
    private long _mixDebugSamples;
    private double _mixDebugPulseOutSum;
    private double _mixDebugTriangleOutSum;
    private double _mixDebugNoiseOutSum;
    private double _mixDebugDmcOutSum;
    private double _mixDebugPreFilterSum;
    private double _mixDebugPostFilterSum;
    private int _pendingCpuStallCycles;

    public void Reset()
    {
        _sampleQueue.Clear();
        _pulse1.Reset();
        _pulse2.Reset();
        _triangle.Reset();
        _noise.Reset();
        _dmc.Reset();
        _frameCounterCycles = 0;
        _fiveStepMode = false;
        _frameIrqInhibit = false;
        _frameIrqPending = false;
        _apuCycleOdd = false;
        _lastApuCycleWasPut = false;
        _frameIrqClearPendingOnPutToGet = false;
        _sampleAccumulator = 0;
        _hpPrevInput = 0f;
        _hpPrevOutput = 0f;
        _lpOutput = 0f;
        _debugSampleCount = 0;
        _debugPulseNonZero = 0;
        _debugTriangleNonZero = 0;
        _debugNoiseNonZero = 0;
        _debugDmcNonZero = 0;
        _debugPulseSum = 0.0;
        _debugTriangleSum = 0.0;
        _debugNoiseSum = 0.0;
        _debugDmcSum = 0.0;
        _mixDebugSamples = 0;
        _mixDebugPulseOutSum = 0.0;
        _mixDebugTriangleOutSum = 0.0;
        _mixDebugNoiseOutSum = 0.0;
        _mixDebugDmcOutSum = 0.0;
        _mixDebugPreFilterSum = 0.0;
        _mixDebugPostFilterSum = 0.0;
        _pendingCpuStallCycles = 0;
    }

    public void ConnectCpuReader(Func<ushort, byte> cpuRead)
    {
        _cpuRead = cpuRead;
    }

    public void ClockCpu()
    {
        _triangle.ClockTimer();
        var apuCycle = _apuCycleOdd;
        var isPutToGetTransition = _lastApuCycleWasPut && !apuCycle;
        if (_frameIrqClearPendingOnPutToGet && isPutToGetTransition)
        {
            _frameIrqPending = false;
            _frameIrqClearPendingOnPutToGet = false;
        }

        _frameCounterCycles++;

        if (apuCycle)
        {
            _pulse1.ClockTimer();
            _pulse2.ClockTimer();
            _noise.ClockTimer();
            // Frame counter timing (current implementation): advance on APU cycles.
            if (_fiveStepMode)
            {
                if (_frameCounterCycles is 3729 or 11186)
                {
                    ClockQuarterFrame();
                }
                else if (_frameCounterCycles is 7457 or 18641)
                {
                    ClockQuarterFrame();
                    ClockHalfFrame();
                }

                if (_frameCounterCycles >= 18641)
                {
                    _frameCounterCycles = 0;
                }
            }
            else
            {
                if (_frameCounterCycles is 3729 or 11186)
                {
                    ClockQuarterFrame();
                }
                else if (_frameCounterCycles is 7457 or 14915)
                {
                    ClockQuarterFrame();
                    ClockHalfFrame();
                }

                if (_frameCounterCycles >= 14915)
                {
                    _frameCounterCycles = 0;
                    if (!_frameIrqInhibit)
                    {
                        _frameIrqPending = true;
                    }
                }
            }
        }

        _dmc.ClockCpu(_cpuRead);
        _pendingCpuStallCycles += _dmc.ConsumeStallCyclesRequested();
        _lastApuCycleWasPut = apuCycle;
        _apuCycleOdd = !_apuCycleOdd;

        _sampleAccumulator += SampleRate;
        if (_sampleAccumulator < CpuFrequency)
        {
            return;
        }

        _sampleAccumulator -= CpuFrequency;
        EnqueueSample(MixOutput());
    }

    public bool ConsumeCpuStallCycle()
    {
        if (_pendingCpuStallCycles <= 0)
        {
            return false;
        }

        _pendingCpuStallCycles--;
        return true;
    }

    public bool ConsumeIrq()
    {
        return _frameIrqPending || _dmc.IsIrqPending;
    }

    public byte ReadStatus(byte openBus)
    {
        byte status = 0;
        if (_pulse1.IsActive) status |= 1 << 0;
        if (_pulse2.IsActive) status |= 1 << 1;
        if (_triangle.IsActive) status |= 1 << 2;
        if (_noise.IsActive) status |= 1 << 3;
        if (_dmc.IsActive) status |= 1 << 4;
        if (_frameIrqPending) status |= 1 << 6;
        if (_dmc.IsIrqPending) status |= 1 << 7;

        _frameIrqPending = false;
        // On $4015 reads, only bit 5 reflects CPU open bus; status bits are driven by APU.
        return (byte)((openBus & 0x20) | status);
    }

    public void WriteRegister(ushort address, byte value)
    {
        switch (address)
        {
            case 0x4000: _pulse1.WriteControl(value); break;
            case 0x4001: _pulse1.WriteSweep(value); break;
            case 0x4002: _pulse1.WriteTimerLow(value); break;
            case 0x4003: _pulse1.WriteTimerHigh(value); break;
            case 0x4004: _pulse2.WriteControl(value); break;
            case 0x4005: _pulse2.WriteSweep(value); break;
            case 0x4006: _pulse2.WriteTimerLow(value); break;
            case 0x4007: _pulse2.WriteTimerHigh(value); break;

            case 0x4008: _triangle.WriteControl(value); break;
            case 0x400A: _triangle.WriteTimerLow(value); break;
            case 0x400B: _triangle.WriteTimerHigh(value); break;

            case 0x400C: _noise.WriteControl(value); break;
            case 0x400E: _noise.WritePeriod(value); break;
            case 0x400F: _noise.WriteLength(value); break;

            case 0x4010: _dmc.WriteControl(value); break;
            case 0x4011: _dmc.WriteDirectLoad(value); break;
            case 0x4012: _dmc.WriteSampleAddress(value); break;
            case 0x4013: _dmc.WriteSampleLength(value); break;

            case 0x4015:
                _dmc.ClearIrq();
                _pulse1.SetEnabled((value & 0x01) != 0);
                _pulse2.SetEnabled((value & 0x02) != 0);
                _triangle.SetEnabled((value & 0x04) != 0);
                _noise.SetEnabled((value & 0x08) != 0);
                _dmc.SetEnabled((value & 0x10) != 0);
                break;
            case 0x4017:
                _fiveStepMode = (value & 0x80) != 0;
                _frameIrqInhibit = (value & 0x40) != 0;
                if (_frameIrqInhibit)
                {
                    _frameIrqClearPendingOnPutToGet = true;
                }
                _frameCounterCycles = 0;
                if (_fiveStepMode)
                {
                    ClockQuarterFrame();
                    ClockHalfFrame();
                }
                break;
        }
    }

    public int DrainSamples(float[] destination, int maxSamples)
    {
        var count = Math.Min(maxSamples, destination.Length);
        var written = 0;
        while (written < count && _sampleQueue.Count > 0)
        {
            destination[written++] = _sampleQueue.Dequeue();
        }

        return written;
    }

    public AudioChannelDebugSnapshot DrainAudioChannelDebugSnapshot()
    {
        var snapshot = new AudioChannelDebugSnapshot(
            _debugSampleCount,
            _debugPulseNonZero,
            _debugTriangleNonZero,
            _debugNoiseNonZero,
            _debugDmcNonZero,
            _debugPulseSum,
            _debugTriangleSum,
            _debugNoiseSum,
            _debugDmcSum);

        _debugSampleCount = 0;
        _debugPulseNonZero = 0;
        _debugTriangleNonZero = 0;
        _debugNoiseNonZero = 0;
        _debugDmcNonZero = 0;
        _debugPulseSum = 0.0;
        _debugTriangleSum = 0.0;
        _debugNoiseSum = 0.0;
        _debugDmcSum = 0.0;
        return snapshot;
    }

    public NoiseDebugSnapshot DrainNoiseDebugSnapshot()
    {
        return _noise.DrainDebugSnapshot();
    }

    public MixDebugSnapshot DrainMixDebugSnapshot()
    {
        var snapshot = new MixDebugSnapshot(
            _mixDebugSamples,
            _mixDebugPulseOutSum,
            _mixDebugTriangleOutSum,
            _mixDebugNoiseOutSum,
            _mixDebugDmcOutSum,
            _mixDebugPreFilterSum,
            _mixDebugPostFilterSum);

        _mixDebugSamples = 0;
        _mixDebugPulseOutSum = 0.0;
        _mixDebugTriangleOutSum = 0.0;
        _mixDebugNoiseOutSum = 0.0;
        _mixDebugDmcOutSum = 0.0;
        _mixDebugPreFilterSum = 0.0;
        _mixDebugPostFilterSum = 0.0;
        return snapshot;
    }

    private void ClockQuarterFrame()
    {
        _pulse1.ClockEnvelope();
        _pulse2.ClockEnvelope();
        _triangle.ClockLinearCounter();
        _noise.ClockEnvelope();
    }

    private void ClockHalfFrame()
    {
        _pulse1.ClockLengthAndSweep();
        _pulse2.ClockLengthAndSweep();
        _triangle.ClockLengthCounter();
        _noise.ClockLengthCounter();
    }

    private void EnqueueSample(float sample)
    {
        if (_sampleQueue.Count >= MaxQueuedSamples)
        {
            _sampleQueue.Dequeue();
        }

        _sampleQueue.Enqueue(sample);
    }

    private float MixOutput()
    {
        var p1 = _pulse1.Sample();
        var p2 = _pulse2.Sample();
        var tri = _triangle.Sample();
        var noi = _noise.Sample();
        var dmc = _dmc.Sample();
        var pulseSum = p1 + p2;

        _debugSampleCount++;
        if (pulseSum > 0f) _debugPulseNonZero++;
        if (tri > 0f) _debugTriangleNonZero++;
        if (noi > 0f) _debugNoiseNonZero++;
        if (dmc > 0f) _debugDmcNonZero++;
        _debugPulseSum += pulseSum;
        _debugTriangleSum += tri;
        _debugNoiseSum += noi;
        _debugDmcSum += dmc;

        var pulseOut = pulseSum > 0f
            ? 95.88f / ((8128f / pulseSum) + 100f)
            : 0f;

        var triTerm = tri / 8227f;
        var noiseTerm = noi / 12241f;
        var dmcTerm = dmc / 22638f;
        var tndInput = triTerm + noiseTerm + dmcTerm;
        var tndOut = tndInput > 0f
            ? 159.79f / (100f + (1f / tndInput))
            : 0f;
        var mixed = pulseOut + tndOut;

        var triShare = tndInput > 0f ? triTerm / tndInput : 0f;
        var noiseShare = tndInput > 0f ? noiseTerm / tndInput : 0f;
        var dmcShare = tndInput > 0f ? dmcTerm / tndInput : 0f;
        var triOut = tndOut * triShare;
        var noiseOut = tndOut * noiseShare;
        var dmcOut = tndOut * dmcShare;

        // Simple NES-like post-filtering: remove DC, then smooth harsh aliasing a bit.
        const float highPassCoeff = 0.996f;
        const float lowPassCoeff = 0.815f;
        var highPassed = highPassCoeff * (_hpPrevOutput + mixed - _hpPrevInput);
        _hpPrevInput = mixed;
        _hpPrevOutput = highPassed;

        _lpOutput += lowPassCoeff * (highPassed - _lpOutput);
var postFilter = Math.Clamp(_lpOutput, -1.0f, 1.0f);
        _mixDebugSamples++;
        _mixDebugPulseOutSum += pulseOut;
        _mixDebugTriangleOutSum += triOut;
        _mixDebugNoiseOutSum += noiseOut;
        _mixDebugDmcOutSum += dmcOut;
        _mixDebugPreFilterSum += mixed;
        _mixDebugPostFilterSum += postFilter;

        return postFilter;
    }

    public readonly record struct AudioChannelDebugSnapshot(
        long Samples,
        long PulseNonZero,
        long TriangleNonZero,
        long NoiseNonZero,
        long DmcNonZero,
        double PulseSum,
        double TriangleSum,
        double NoiseSum,
        double DmcSum);

    public readonly record struct NoiseDebugSnapshot(
        int PeriodIndex,
        int PeriodValue,
        bool ModeShort,
        int TimerCounter,
        byte LengthCounter,
        byte EnvelopeDecay,
        bool ConstantVolume,
        byte EnvelopePeriod,
        long TimerReloads,
        long ShortModeReloads,
        long PeriodWrites,
        long ModeChanges,
        long PeriodIndexChanges);

    public readonly record struct MixDebugSnapshot(
        long Samples,
        double PulseOutSum,
        double TriangleOutSum,
        double NoiseOutSum,
        double DmcOutSum,
        double PreFilterSum,
        double PostFilterSum);

    private sealed class PulseChannel
    {
        private static readonly byte[,] DutySequences =
        {
            { 0, 1, 0, 0, 0, 0, 0, 0 },
            { 0, 1, 1, 0, 0, 0, 0, 0 },
            { 0, 1, 1, 1, 1, 0, 0, 0 },
            { 1, 0, 0, 1, 1, 1, 1, 1 }
        };

        private readonly bool _onesComplementNegate;
        private byte _duty;
        private ushort _timer;
        private ushort _timerCounter;
        private byte _sequencerStep;

        private bool _lengthHalt;
        private bool _constantVolume;
        private byte _envelopePeriod;
        private bool _envelopeStart;
        private byte _envelopeDivider;
        private byte _envelopeDecay;

        private bool _sweepEnabled;
        private byte _sweepPeriod;
        private bool _sweepNegate;
        private byte _sweepShift;
        private bool _sweepReload;
        private byte _sweepDivider;

        private byte _lengthCounter;
        private bool _enabled;

        public PulseChannel(bool onesComplementNegate)
        {
            _onesComplementNegate = onesComplementNegate;
        }

        public bool IsActive => _enabled && _lengthCounter > 0;

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled)
            {
                _lengthCounter = 0;
            }
        }

        public void Reset()
        {
            _duty = 0;
            _timer = 0;
            _timerCounter = 0;
            _sequencerStep = 0;
            _lengthHalt = false;
            _constantVolume = false;
            _envelopePeriod = 0;
            _envelopeStart = false;
            _envelopeDivider = 0;
            _envelopeDecay = 0;
            _sweepEnabled = false;
            _sweepPeriod = 0;
            _sweepNegate = false;
            _sweepShift = 0;
            _sweepReload = false;
            _sweepDivider = 0;
            _lengthCounter = 0;
            _enabled = false;
        }

        public void WriteControl(byte value)
        {
            _duty = (byte)((value >> 6) & 0x03);
            _lengthHalt = (value & 0x20) != 0;
            _constantVolume = (value & 0x10) != 0;
            _envelopePeriod = (byte)(value & 0x0F);
            _envelopeStart = true;
        }

        public void WriteSweep(byte value)
        {
            _sweepEnabled = (value & 0x80) != 0;
            _sweepPeriod = (byte)((value >> 4) & 0x07);
            _sweepNegate = (value & 0x08) != 0;
            _sweepShift = (byte)(value & 0x07);
            _sweepReload = true;
        }

        public void WriteTimerLow(byte value)
        {
            _timer = (ushort)((_timer & 0x0700) | value);
        }

        public void WriteTimerHigh(byte value)
        {
            _timer = (ushort)((_timer & 0x00FF) | ((value & 0x07) << 8));
            if (_enabled)
            {
                _lengthCounter = LengthTable[(value >> 3) & 0x1F];
            }

            _envelopeStart = true;
            _sequencerStep = 0;
            _timerCounter = _timer;
        }

        public void ClockTimer()
        {
            if (_timerCounter == 0)
            {
                _timerCounter = _timer;
                _sequencerStep = (byte)((_sequencerStep + 1) & 0x07);
                return;
            }

            _timerCounter--;
        }

        public void ClockEnvelope()
        {
            if (_envelopeStart)
            {
                _envelopeStart = false;
                _envelopeDecay = 15;
                _envelopeDivider = _envelopePeriod;
                return;
            }

            if (_envelopeDivider > 0)
            {
                _envelopeDivider--;
                return;
            }

            _envelopeDivider = _envelopePeriod;
            if (_envelopeDecay > 0)
            {
                _envelopeDecay--;
            }
            else if (_lengthHalt)
            {
                _envelopeDecay = 15;
            }
        }

        public void ClockLengthAndSweep()
        {
            if (!_lengthHalt && _lengthCounter > 0)
            {
                _lengthCounter--;
            }

            if (_sweepReload)
            {
                _sweepReload = false;
                _sweepDivider = _sweepPeriod;
                if (_sweepEnabled && _sweepShift > 0)
                {
                    ApplySweep();
                }
                return;
            }

            if (_sweepDivider > 0)
            {
                _sweepDivider--;
                return;
            }

            _sweepDivider = _sweepPeriod;
            if (_sweepEnabled && _sweepShift > 0)
            {
                ApplySweep();
            }
        }

        public float Sample()
        {
            if (!_enabled || _lengthCounter == 0 || IsSweepMuted())
            {
                return 0f;
            }

            var volume = _constantVolume ? _envelopePeriod : _envelopeDecay;
            if (volume == 0)
            {
                return 0f;
            }

            return DutySequences[_duty, _sequencerStep] != 0 ? volume : 0f;
        }

        private void ApplySweep()
        {
            var target = CalculateSweepTarget();
            if (target >= 0 && target <= 0x7FF)
            {
                _timer = (ushort)target;
            }
        }

        private bool IsSweepMuted()
        {
            return _timer < 8 || CalculateSweepTarget() > 0x7FF;
        }

        private int CalculateSweepTarget()
        {
            var change = _timer >> _sweepShift;
            if (_sweepNegate)
            {
                var correction = _onesComplementNegate ? 1 : 0;
                return _timer - change - correction;
            }

            return _timer + change;
        }
    }

    private sealed class TriangleChannel
    {
        private static readonly byte[] Sequence =
        {
            15, 14, 13, 12, 11, 10, 9, 8,
            7, 6, 5, 4, 3, 2, 1, 0,
            0, 1, 2, 3, 4, 5, 6, 7,
            8, 9, 10, 11, 12, 13, 14, 15
        };

        private ushort _timer;
        private ushort _timerCounter;
        private byte _sequencerStep;
        private bool _enabled;
        private byte _lengthCounter;
        private bool _controlFlag;
        private byte _linearReloadValue;
        private byte _linearCounter;
        private bool _linearReloadFlag;

        public bool IsActive => _enabled && _lengthCounter > 0;

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled)
            {
                _lengthCounter = 0;
            }
        }

        public void Reset()
        {
            _timer = 0;
            _timerCounter = 0;
            _sequencerStep = 0;
            _enabled = false;
            _lengthCounter = 0;
            _controlFlag = false;
            _linearReloadValue = 0;
            _linearCounter = 0;
            _linearReloadFlag = false;
        }

        public void WriteControl(byte value)
        {
            _controlFlag = (value & 0x80) != 0;
            _linearReloadValue = (byte)(value & 0x7F);
        }

        public void WriteTimerLow(byte value)
        {
            _timer = (ushort)((_timer & 0x0700) | value);
        }

        public void WriteTimerHigh(byte value)
        {
            _timer = (ushort)((_timer & 0x00FF) | ((value & 0x07) << 8));
            if (_enabled)
            {
                _lengthCounter = LengthTable[(value >> 3) & 0x1F];
            }

            _linearReloadFlag = true;
            _timerCounter = _timer;
        }

        public void ClockTimer()
        {
            if (_timerCounter == 0)
            {
                _timerCounter = _timer;
                if (_enabled && _timer >= 2 && _lengthCounter > 0 && _linearCounter > 0)
                {
                    _sequencerStep = (byte)((_sequencerStep + 1) & 0x1F);
                }

                return;
            }

            _timerCounter--;
        }

        public void ClockLinearCounter()
        {
            if (_linearReloadFlag)
            {
                _linearCounter = _linearReloadValue;
            }
            else if (_linearCounter > 0)
            {
                _linearCounter--;
            }

            if (!_controlFlag)
            {
                _linearReloadFlag = false;
            }
        }

        public void ClockLengthCounter()
        {
            if (!_controlFlag && _lengthCounter > 0)
            {
                _lengthCounter--;
            }
        }

        public float Sample()
        {
            if (!_enabled || _timer < 2 || _lengthCounter == 0 || _linearCounter == 0)
            {
                return 0f;
            }

            return Sequence[_sequencerStep];
        }
    }

    private sealed class NoiseChannel
    {
        private static readonly int[] PeriodByIndex =
        {
            4, 8, 16, 32, 64, 96, 128, 160,
            202, 254, 380, 508, 762, 1016, 2034, 4068
        };

        private int _periodIndex;
        private bool _mode;
        private ushort _lfsr = 1;
        private int _timerCounter;
        private bool _enabled;
        private byte _lengthCounter;
        private bool _lengthHalt;
        private bool _constantVolume;
        private byte _envelopePeriod;
        private bool _envelopeStart;
        private byte _envelopeDivider;
        private byte _envelopeDecay;
        private long _debugTimerReloads;
        private long _debugShortModeReloads;
        private long _debugPeriodWrites;
        private long _debugModeChanges;
        private long _debugPeriodIndexChanges;

        public bool IsActive => _enabled && _lengthCounter > 0;

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled)
            {
                _lengthCounter = 0;
            }
        }

        public void Reset()
        {
            _periodIndex = 0;
            _mode = false;
            _lfsr = 1;
            _timerCounter = 0;
            _enabled = false;
            _lengthCounter = 0;
            _lengthHalt = false;
            _constantVolume = false;
            _envelopePeriod = 0;
            _envelopeStart = false;
            _envelopeDivider = 0;
            _envelopeDecay = 0;
            _debugTimerReloads = 0;
            _debugShortModeReloads = 0;
            _debugPeriodWrites = 0;
            _debugModeChanges = 0;
            _debugPeriodIndexChanges = 0;
        }

        public void WriteControl(byte value)
        {
            _lengthHalt = (value & 0x20) != 0;
            _constantVolume = (value & 0x10) != 0;
            _envelopePeriod = (byte)(value & 0x0F);
            _envelopeStart = true;
        }

        public void WritePeriod(byte value)
        {
            var newMode = (value & 0x80) != 0;
            var newPeriodIndex = value & 0x0F;
            _debugPeriodWrites++;
            if (newMode != _mode)
            {
                _debugModeChanges++;
            }

            if (newPeriodIndex != _periodIndex)
            {
                _debugPeriodIndexChanges++;
            }

            _mode = newMode;
            _periodIndex = newPeriodIndex;
        }

        public void ClockTimer()
        {
            if (_timerCounter == 0)
            {
                _timerCounter = PeriodByIndex[_periodIndex];
                ClockLfsr();
                _debugTimerReloads++;
                if (_mode)
                {
                    _debugShortModeReloads++;
                }

                return;
            }

            _timerCounter--;
        }

        public void WriteLength(byte value)
        {
            if (_enabled)
            {
                _lengthCounter = LengthTable[(value >> 3) & 0x1F];
            }

            _envelopeStart = true;
        }

        public void ClockEnvelope()
        {
            if (_envelopeStart)
            {
                _envelopeStart = false;
                _envelopeDecay = 15;
                _envelopeDivider = _envelopePeriod;
                return;
            }

            if (_envelopeDivider > 0)
            {
                _envelopeDivider--;
                return;
            }

            _envelopeDivider = _envelopePeriod;
            if (_envelopeDecay > 0)
            {
                _envelopeDecay--;
            }
            else if (_lengthHalt)
            {
                _envelopeDecay = 15;
            }
        }

        public void ClockLengthCounter()
        {
            if (!_lengthHalt && _lengthCounter > 0)
            {
                _lengthCounter--;
            }
        }

        public float Sample()
        {
            if (!_enabled || _lengthCounter == 0)
            {
                return 0f;
            }

            var volume = _constantVolume ? _envelopePeriod : _envelopeDecay;
            if (volume == 0)
            {
                return 0f;
            }

            return (_lfsr & 0x0001) != 0 ? volume : 0f;
        }

        private void ClockLfsr()
        {
            var bit0 = _lfsr & 0x0001;
            var tap = _mode ? ((_lfsr >> 6) & 0x0001) : ((_lfsr >> 1) & 0x0001);
            var feedback = bit0 ^ tap;
            _lfsr >>= 1;
            _lfsr |= (ushort)(feedback << 14);
        }

        public NoiseDebugSnapshot DrainDebugSnapshot()
        {
            var snapshot = new NoiseDebugSnapshot(
                _periodIndex,
                PeriodByIndex[_periodIndex],
                _mode,
                _timerCounter,
                _lengthCounter,
                _envelopeDecay,
                _constantVolume,
                _envelopePeriod,
                _debugTimerReloads,
                _debugShortModeReloads,
                _debugPeriodWrites,
                _debugModeChanges,
                _debugPeriodIndexChanges);

            _debugTimerReloads = 0;
            _debugShortModeReloads = 0;
            _debugPeriodWrites = 0;
            _debugModeChanges = 0;
            _debugPeriodIndexChanges = 0;
            return snapshot;
        }
    }

    private sealed class DmcChannel
    {
        private static readonly int[] RateTable =
        {
            428, 380, 340, 320, 286, 254, 226, 214,
            190, 160, 142, 128, 106, 85, 72, 54
        };

        private byte _outputLevel;
        private bool _enabled;
        private bool _irqEnabled;
        private bool _loop;
        private int _timerPeriod = RateTable[0];
        private int _timerCounter;
        private ushort _sampleAddress = 0xC000;
        private ushort _currentAddress = 0xC000;
        private ushort _sampleLength = 1;
        private ushort _bytesRemaining;
        private byte _shiftRegister;
        private byte _bitsRemaining = 8;
        private byte _sampleBuffer;
        private bool _sampleBufferEmpty = true;
        private bool _silence = true;
        private bool _irqPending;
        private int _stallCyclesRequested;

        public bool IsActive => _bytesRemaining > 0;
        public bool IsIrqPending => _irqPending;

        public void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled)
            {
                _bytesRemaining = 0;
                return;
            }

            if (_bytesRemaining == 0)
            {
                RestartSample();
            }
        }

        public void Reset()
        {
            _outputLevel = 0;
            _enabled = false;
            _irqEnabled = false;
            _loop = false;
            _timerPeriod = RateTable[0];
            _timerCounter = 0;
            _sampleAddress = 0xC000;
            _currentAddress = 0xC000;
            _sampleLength = 1;
            _bytesRemaining = 0;
            _shiftRegister = 0;
            _bitsRemaining = 8;
            _sampleBuffer = 0;
            _sampleBufferEmpty = true;
            _silence = true;
            _irqPending = false;
            _stallCyclesRequested = 0;
        }

        public void WriteControl(byte value)
        {
            _irqEnabled = (value & 0x80) != 0;
            _loop = (value & 0x40) != 0;
            _timerPeriod = RateTable[value & 0x0F];
            if (!_irqEnabled)
            {
                _irqPending = false;
            }
        }

        public void WriteDirectLoad(byte value)
        {
            _outputLevel = (byte)(value & 0x7F);
        }

        public void WriteSampleAddress(byte value)
        {
            _sampleAddress = (ushort)(0xC000 + (value << 6));
        }

        public void WriteSampleLength(byte value)
        {
            _sampleLength = (ushort)((value << 4) | 0x0001);
        }

        public void ClearIrq()
        {
            _irqPending = false;
        }

        public void ClockCpu(Func<ushort, byte>? cpuRead)
        {
            if (_timerCounter == 0)
            {
                _timerCounter = _timerPeriod;
                ClockOutputUnit();
            }
            else
            {
                _timerCounter--;
            }

            TryRefillSampleBuffer(cpuRead);
        }

        public float Sample()
        {
            if (!_enabled)
            {
                return 0f;
            }

            return _outputLevel;
        }

        private void ClockOutputUnit()
        {
            if (!_silence)
            {
                if ((_shiftRegister & 0x01) != 0)
                {
                    if (_outputLevel <= 125)
                    {
                        _outputLevel += 2;
                    }
                }
                else if (_outputLevel >= 2)
                {
                    _outputLevel -= 2;
                }
            }

            _shiftRegister >>= 1;
            if (_bitsRemaining > 0)
            {
                _bitsRemaining--;
            }

            if (_bitsRemaining != 0)
            {
                return;
            }

            _bitsRemaining = 8;
            if (_sampleBufferEmpty)
            {
                _silence = true;
                return;
            }

            _silence = false;
            _shiftRegister = _sampleBuffer;
            _sampleBufferEmpty = true;
        }

        private void TryRefillSampleBuffer(Func<ushort, byte>? cpuRead)
        {
            if (_sampleBufferEmpty && _bytesRemaining > 0 && cpuRead is not null)
            {
                _sampleBuffer = cpuRead(_currentAddress);
                _sampleBufferEmpty = false;
                _stallCyclesRequested += 4;

                _currentAddress = _currentAddress == 0xFFFF
                    ? (ushort)0x8000
                    : (ushort)(_currentAddress + 1);

                _bytesRemaining--;
                if (_bytesRemaining == 0)
                {
                    if (_loop)
                    {
                        RestartSample();
                    }
                    else if (_irqEnabled)
                    {
                        _irqPending = true;
                    }
                }
            }
        }

        public int ConsumeStallCyclesRequested()
        {
            var pending = _stallCyclesRequested;
            _stallCyclesRequested = 0;
            return pending;
        }

        private void RestartSample()
        {
            _currentAddress = _sampleAddress;
            _bytesRemaining = _sampleLength;
        }
    }
}
