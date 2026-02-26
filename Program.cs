using cunes.Core;
using cunes.Tests;
using System.IO;

if (args.Contains("--test", StringComparer.OrdinalIgnoreCase))
{
    var ok = CpuSelfTests.RunAll();
    Environment.ExitCode = ok ? 0 : 1;
    return;
}

string? romPath = null;
var romTestMode = false;
int? targetFps = null;
int? windowScale = null;
var audioDebug = false;
var noiseDebug = false;
var mixDebug = false;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg.Equals("--rom", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length)
        {
            Console.WriteLine("Usage: dotnet run -- --rom <path-to-rom.nes>");
            Environment.ExitCode = 1;
            return;
        }

        romPath = args[++i];
    }
    else if (arg.StartsWith("--rom=", StringComparison.OrdinalIgnoreCase))
    {
        romPath = arg["--rom=".Length..];
    }
    else if (arg.Equals("--rom-test", StringComparison.OrdinalIgnoreCase))
    {
        romTestMode = true;
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            romPath = args[++i];
        }
    }
    else if (arg.StartsWith("--rom-test=", StringComparison.OrdinalIgnoreCase))
    {
        romTestMode = true;
        romPath = arg["--rom-test=".Length..];
    }
    else if (arg.Equals("--fps", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length || !int.TryParse(args[++i], out var parsedFps) || parsedFps <= 0)
        {
            Console.WriteLine("Usage: dotnet run -- --fps <positive-integer>");
            Environment.ExitCode = 1;
            return;
        }

        targetFps = parsedFps;
    }
    else if (arg.StartsWith("--fps=", StringComparison.OrdinalIgnoreCase))
    {
        var value = arg["--fps=".Length..];
        if (!int.TryParse(value, out var parsedFps) || parsedFps <= 0)
        {
            Console.WriteLine("Usage: dotnet run -- --fps=<positive-integer>");
            Environment.ExitCode = 1;
            return;
        }

        targetFps = parsedFps;
    }
    else if (arg.Equals("--scale", StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length || !int.TryParse(args[++i], out var parsedScale) || parsedScale <= 0)
        {
            Console.WriteLine("Usage: dotnet run -- --scale <positive-integer>");
            Environment.ExitCode = 1;
            return;
        }

        windowScale = parsedScale;
    }
    else if (arg.StartsWith("--scale=", StringComparison.OrdinalIgnoreCase))
    {
        var value = arg["--scale=".Length..];
        if (!int.TryParse(value, out var parsedScale) || parsedScale <= 0)
        {
            Console.WriteLine("Usage: dotnet run -- --scale=<positive-integer>");
            Environment.ExitCode = 1;
            return;
        }

        windowScale = parsedScale;
    }
    else if (arg.Equals("--audio-debug", StringComparison.OrdinalIgnoreCase))
    {
        audioDebug = true;
    }
    else if (arg.Equals("--noise-debug", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--noise", StringComparison.OrdinalIgnoreCase))
    {
        noiseDebug = true;
    }
    else if (arg.Equals("--mix-debug", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--mix", StringComparison.OrdinalIgnoreCase))
    {
        mixDebug = true;
    }
}

if (romTestMode && string.IsNullOrWhiteSpace(romPath))
{
    var searchDirs = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), "rom"),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "rom"))
    };

    var candidates = new List<string>();
    foreach (var dir in searchDirs.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (!Directory.Exists(dir))
        {
            continue;
        }

        candidates.AddRange(Directory.GetFiles(dir, "*.nes", SearchOption.TopDirectoryOnly));
    }

    if (candidates.Count == 1)
    {
        romPath = candidates[0];
    }
    else if (candidates.Count > 1)
    {
        Console.WriteLine("Multiple ROMs found. Please pass one explicitly:");
        foreach (var candidate in candidates)
        {
            Console.WriteLine($" - {candidate}");
        }

        Environment.ExitCode = 1;
        return;
    }
}

if (romTestMode && string.IsNullOrWhiteSpace(romPath))
{
    Console.WriteLine("Usage: dotnet run -- --rom-test <path-to-rom.nes>");
    Console.WriteLine("Tip: if ./rom contains exactly one .nes file, --rom-test without path works.");
    Environment.ExitCode = 1;
    return;
}

var config = new NesConfig
{
    CpuFrequencyHz = 1_789_773,
    TargetFps = targetFps ?? 60,
    WindowScale = windowScale ?? 3,
    EnableAudioDebug = audioDebug,
    EnableNoiseDebug = noiseDebug,
    EnableMixDebug = mixDebug
};

var app = new NesApp(config, romPath, romTestMode);
try
{
    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Environment.ExitCode = 1;
}
