namespace cunes.Core;

public sealed class NesConfig
{
    public int CpuFrequencyHz { get; init; } = 1_789_773;
    public int TargetFps { get; init; } = 60;
    public bool EnableWindow { get; init; } = true;
    public int WindowScale { get; init; } = 3;
    public string WindowTitle { get; init; } = "CuNES";
    public bool EnableAudioDebug { get; init; }
    public bool EnableNoiseDebug { get; init; }
    public bool EnableMixDebug { get; init; }
}
