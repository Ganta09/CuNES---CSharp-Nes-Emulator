using cunes.Ppu;
using Raylib_cs;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Color = Raylib_cs.Color;
using Rectangle = Raylib_cs.Rectangle;

namespace cunes.Frontend;

public sealed class RaylibRenderer : IRenderer
{
    private const int AudioSampleRate = 44_100;
    private const int AudioChunkSize = 512;
    private const int MaxQueuedAudioSamples = AudioChunkSize * 6;
    private const int AudioPrimeSamples = AudioChunkSize * 2;
    private const int TargetQueuedAudioSamples = AudioChunkSize * 3;

    private readonly Texture2D _texture;
    private readonly Rectangle _sourceRect;
    private readonly Rectangle _destinationRect;
    private readonly AudioStream _audioStream = default;
    private readonly Queue<float> _audioQueue = new();
    private readonly float[] _audioChunk = new float[AudioChunkSize];
    private readonly bool _audioEnabled;
    private readonly bool _audioDebugEnabled;
    private readonly Stopwatch _audioDebugStopwatch = new();
    private readonly Queue<UiAction> _uiActions = new();
    private string _windowTitle = string.Empty;

    private bool _disposed;
    private bool _audioPrimed;
    private bool _contextMenuOpen;
    private bool _keyBindPanelOpen;
    private bool _romLoaded;
    private int _bindingCaptureIndex = -1;
    private float _lastSample;
    private long _debugLastLogMs;
    private long _debugSubmittedSamples;
    private long _debugUnderrunSamples;
    private long _debugDroppedSamples;
    private long _debugChunksPushed;
    private long _debugQueuePeak;
    private double _debugSumSquares;
    private float _debugMinSample = float.MaxValue;
    private float _debugMaxSample = float.MinValue;
    private int _debugClipCount;
    private Vector2 _contextMenuPosition;
    private readonly Vector2 _persistentMenuPosition = new(12, 12);
    private readonly KeyboardKey[] _playerOneBindings = CreateDefaultBindings();

    private const int MenuItemHeight = 26;
    private const int MenuWidth = 170;
    private const int MenuPadding = 6;
    private const int KeyBindPanelWidth = 360;
    private const int KeyBindPanelRowHeight = 28;
    private const int KeyBindPanelPadding = 10;
    private const int KeyBindHeaderHeight = 34;

    private static readonly string[] NesButtonLabels =
    {
        "A", "B", "Select", "Start", "Up", "Down", "Left", "Right"
    };

    private RaylibRenderer(int scale, int targetFps, string title, bool audioDebugEnabled)
    {
        var width = Ppu2C02.ScreenWidth * scale;
        var height = Ppu2C02.ScreenHeight * scale;
        _audioDebugEnabled = audioDebugEnabled;

        Raylib.SetConfigFlags(ConfigFlags.VSyncHint);
        Raylib.InitWindow(width, height, title);
        // Avoid double throttling (VSync + software FPS limiter), which can add micro-stutter.
        Raylib.SetTargetFPS(0);
        _windowTitle = title;

        var image = Raylib.GenImageColor(Ppu2C02.ScreenWidth, Ppu2C02.ScreenHeight, Color.Black);
        _texture = Raylib.LoadTextureFromImage(image);
        Raylib.UnloadImage(image);

        _sourceRect = new Rectangle(0, 0, Ppu2C02.ScreenWidth, Ppu2C02.ScreenHeight);
        _destinationRect = new Rectangle(0, 0, width, height);

        try
        {
            Raylib.InitAudioDevice();
            Raylib.SetAudioStreamBufferSizeDefault(AudioChunkSize);
            _audioStream = Raylib.LoadAudioStream(AudioSampleRate, 32, 1);
            Raylib.PlayAudioStream(_audioStream);
            _audioEnabled = true;
            if (_audioDebugEnabled)
            {
                _audioDebugStopwatch.Start();
                Console.WriteLine("Audio debug enabled (1s interval).");
            }
        }
        catch
        {
            _audioEnabled = false;
        }
    }

    public bool IsInteractive => true;

    public bool ShouldClose => _disposed || Raylib.WindowShouldClose();

    public static RaylibRenderer? TryCreate(int scale, int targetFps, string title, bool audioDebugEnabled = false)
    {
        try
        {
            return new RaylibRenderer(scale, targetFps, title, audioDebugEnabled);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Renderer warning: {ex.Message}");
            return null;
        }
    }

    public void DrawFrame(byte[] frameBuffer)
    {
        if (_disposed || ShouldClose)
        {
            return;
        }

        HandleUiInput();
        Raylib.UpdateTexture(_texture, frameBuffer);

        Raylib.BeginDrawing();
        Raylib.ClearBackground(Color.Black);
        Raylib.DrawTexturePro(_texture, _sourceRect, _destinationRect, Vector2.Zero, 0, Color.White);
        DrawContextMenu();
        DrawKeyBindPanel();
        Raylib.EndDrawing();

        PumpAudio();
        MaybeLogAudioDebug();
    }

    public void SubmitAudioSamples(float[] samples, int sampleCount)
    {
        if (!_audioEnabled || _disposed || sampleCount <= 0)
        {
            return;
        }

        var count = Math.Min(sampleCount, samples.Length);
        for (var i = 0; i < count; i++)
        {
            if (_audioQueue.Count >= MaxQueuedAudioSamples)
            {
                _audioQueue.Dequeue();
                _debugDroppedSamples++;
            }

            var sample = samples[i];
            _audioQueue.Enqueue(sample);
            _debugSubmittedSamples++;
            _debugSumSquares += sample * sample;
            if (sample < _debugMinSample) _debugMinSample = sample;
            if (sample > _debugMaxSample) _debugMaxSample = sample;
            if (sample <= -0.999f || sample >= 0.999f) _debugClipCount++;
        }

        if (_audioQueue.Count > _debugQueuePeak)
        {
            _debugQueuePeak = _audioQueue.Count;
        }

        // Keep latency low by trimming backlog beyond the target queue size.
        while (_audioQueue.Count > TargetQueuedAudioSamples)
        {
            _audioQueue.Dequeue();
            _debugDroppedSamples++;
        }

        PumpAudio();
        MaybeLogAudioDebug();
    }

    public void SetWindowTitle(string title)
    {
        if (_disposed)
        {
            return;
        }

        if (string.Equals(_windowTitle, title, StringComparison.Ordinal))
        {
            return;
        }

        _windowTitle = title;
        Raylib.SetWindowTitle(title);
    }

    public byte GetControllerState(int player)
    {
        if (_disposed)
        {
            return 0;
        }

        return player switch
        {
            0 => ReadPlayerOne(),
            _ => 0
        };
    }

    public bool TryDequeueUiAction(out UiAction action)
    {
        if (_uiActions.Count == 0)
        {
            action = default;
            return false;
        }

        action = _uiActions.Dequeue();
        return true;
    }

    public void SetRomLoaded(bool isLoaded)
    {
        _romLoaded = isLoaded;
        if (!_romLoaded)
        {
            _contextMenuOpen = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_audioEnabled)
        {
            Raylib.StopAudioStream(_audioStream);
            Raylib.UnloadAudioStream(_audioStream);
            Raylib.CloseAudioDevice();
        }

        Raylib.UnloadTexture(_texture);
        Raylib.CloseWindow();
        _disposed = true;
    }

    private unsafe void PumpAudio()
    {
        if (!_audioEnabled)
        {
            return;
        }

        // Start only when a small prebuffer exists to reduce immediate underruns.
        if (!_audioPrimed)
        {
            if (_audioQueue.Count < AudioPrimeSamples)
            {
                return;
            }

            _audioPrimed = true;
        }

        while (Raylib.IsAudioStreamProcessed(_audioStream))
        {
            var samplesToPush = Math.Min(AudioChunkSize, _audioQueue.Count);
            if (samplesToPush <= 0)
            {
                break;
            }

            for (var i = 0; i < samplesToPush; i++)
            {
                _lastSample = _audioQueue.Dequeue();
                _audioChunk[i] = _lastSample;
            }

            fixed (float* chunkPtr = _audioChunk)
            {
                Raylib.UpdateAudioStream(_audioStream, chunkPtr, samplesToPush);
            }

            _debugChunksPushed++;
        }
    }

    private void MaybeLogAudioDebug()
    {
        if (!_audioDebugEnabled || !_audioDebugStopwatch.IsRunning)
        {
            return;
        }

        var nowMs = _audioDebugStopwatch.ElapsedMilliseconds;
        if (nowMs - _debugLastLogMs < 1000)
        {
            return;
        }

        var submitted = _debugSubmittedSamples;
        var outputSamples = _debugChunksPushed * AudioChunkSize;
        var queueMs = _audioQueue.Count * 1000.0 / AudioSampleRate;
        var rms = submitted > 0 ? Math.Sqrt(_debugSumSquares / submitted) : 0.0;
        var min = _debugMinSample == float.MaxValue ? 0f : _debugMinSample;
        var max = _debugMaxSample == float.MinValue ? 0f : _debugMaxSample;

        Console.WriteLine(
            $"[audio] in={submitted} samp/s, out={outputSamples} samp/s, q={_audioQueue.Count} ({queueMs:F1} ms), qPeak={_debugQueuePeak}, " +
            $"chunks={_debugChunksPushed}, underrun={_debugUnderrunSamples}, drop={_debugDroppedSamples}, " +
            $"rms={rms:F3}, min={min:F3}, max={max:F3}, clip={_debugClipCount}");

        _debugLastLogMs = nowMs;
        _debugSubmittedSamples = 0;
        _debugUnderrunSamples = 0;
        _debugDroppedSamples = 0;
        _debugChunksPushed = 0;
        _debugQueuePeak = _audioQueue.Count;
        _debugSumSquares = 0;
        _debugMinSample = float.MaxValue;
        _debugMaxSample = float.MinValue;
        _debugClipCount = 0;
    }

    private byte ReadPlayerOne()
    {
        byte state = 0;

        for (var i = 0; i < _playerOneBindings.Length; i++)
        {
            if (Raylib.IsKeyDown(_playerOneBindings[i]))
            {
                state |= (byte)(1 << i);
            }
        }

        return state;
    }

    private void HandleUiInput()
    {
        if (_keyBindPanelOpen)
        {
            HandleKeyBindPanelInput();
            return;
        }

        var menuAlwaysVisible = !_romLoaded;

        if (!menuAlwaysVisible && Raylib.IsMouseButtonPressed(MouseButton.Right))
        {
            _contextMenuOpen = true;
            _contextMenuPosition = Raylib.GetMousePosition();
            return;
        }

        if (!menuAlwaysVisible && !_contextMenuOpen)
        {
            return;
        }

        if (!menuAlwaysVisible && Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            _contextMenuOpen = false;
            return;
        }

        if (!Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            return;
        }

        var mousePos = Raylib.GetMousePosition();
        BuildMenuLayout(menuAlwaysVisible, out var menuRect, out var loadRect, out var closeRect, out var keyBindsRect, out var exitRect);
        if (!Raylib.CheckCollisionPointRec(mousePos, menuRect))
        {
            if (!menuAlwaysVisible)
            {
                _contextMenuOpen = false;
            }

            return;
        }

        if (Raylib.CheckCollisionPointRec(mousePos, loadRect))
        {
            _contextMenuOpen = false;
            var romPath = ShowOpenRomDialog();
            if (!string.IsNullOrWhiteSpace(romPath))
            {
                _uiActions.Enqueue(new UiAction(UiActionType.LoadRom, romPath));
            }

            return;
        }

        if (Raylib.CheckCollisionPointRec(mousePos, closeRect))
        {
            _contextMenuOpen = false;
            _uiActions.Enqueue(new UiAction(UiActionType.CloseRom));
            return;
        }

        if (Raylib.CheckCollisionPointRec(mousePos, keyBindsRect))
        {
            _contextMenuOpen = false;
            _keyBindPanelOpen = true;
            _bindingCaptureIndex = -1;
            return;
        }

        if (Raylib.CheckCollisionPointRec(mousePos, exitRect))
        {
            _contextMenuOpen = false;
            _uiActions.Enqueue(new UiAction(UiActionType.Exit));
        }
    }

    private void DrawContextMenu()
    {
        var menuAlwaysVisible = !_romLoaded;
        if (!menuAlwaysVisible && !_contextMenuOpen)
        {
            return;
        }

        BuildMenuLayout(menuAlwaysVisible, out var menuRect, out var loadRect, out var closeRect, out var keyBindsRect, out var exitRect);
        var mousePos = Raylib.GetMousePosition();

        Raylib.DrawRectangleRec(menuRect, new Color(24, 24, 24, 235));
        Raylib.DrawRectangleLinesEx(menuRect, 1, new Color(210, 210, 210, 255));
        DrawMenuItem("Load ROM", loadRect, mousePos);
        DrawMenuItem("Close ROM", closeRect, mousePos);
        DrawMenuItem("Key Binds", keyBindsRect, mousePos);
        DrawMenuItem("Exit", exitRect, mousePos);
    }

    private void DrawMenuItem(string label, Rectangle rect, Vector2 mousePos)
    {
        var isHovered = Raylib.CheckCollisionPointRec(mousePos, rect);
        if (isHovered)
        {
            Raylib.DrawRectangleRec(rect, new Color(65, 65, 65, 255));
        }

        Raylib.DrawText(label, (int)rect.X + 10, (int)rect.Y + 6, 16, Color.RayWhite);
    }

    private void BuildMenuLayout(bool menuAlwaysVisible, out Rectangle menuRect, out Rectangle loadRect, out Rectangle closeRect, out Rectangle keyBindsRect, out Rectangle exitRect)
    {
        var totalHeight = MenuPadding * 2 + MenuItemHeight * 4;
        var position = menuAlwaysVisible ? _persistentMenuPosition : _contextMenuPosition;
        var x = position.X;
        var y = position.Y;
        var maxX = Raylib.GetScreenWidth() - MenuWidth - 2;
        var maxY = Raylib.GetScreenHeight() - totalHeight - 2;
        x = Math.Clamp(x, 2, Math.Max(2, maxX));
        y = Math.Clamp(y, 2, Math.Max(2, maxY));

        menuRect = new Rectangle(x, y, MenuWidth, totalHeight);
        loadRect = new Rectangle(x + MenuPadding, y + MenuPadding, MenuWidth - (MenuPadding * 2), MenuItemHeight);
        closeRect = new Rectangle(loadRect.X, loadRect.Y + MenuItemHeight, loadRect.Width, MenuItemHeight);
        keyBindsRect = new Rectangle(loadRect.X, closeRect.Y + MenuItemHeight, loadRect.Width, MenuItemHeight);
        exitRect = new Rectangle(loadRect.X, keyBindsRect.Y + MenuItemHeight, loadRect.Width, MenuItemHeight);
    }

    private void DrawKeyBindPanel()
    {
        if (!_keyBindPanelOpen)
        {
            return;
        }

        BuildKeyBindPanelLayout(out var panelRect, out var rows, out var resetRect, out var closeRect);
        var mousePos = Raylib.GetMousePosition();

        Raylib.DrawRectangleRec(panelRect, new Color(22, 22, 22, 240));
        Raylib.DrawRectangleLinesEx(panelRect, 1, new Color(220, 220, 220, 255));
        Raylib.DrawText("Key Binds (Player 1)", (int)panelRect.X + 10, (int)panelRect.Y + 8, 18, Color.RayWhite);

        for (var i = 0; i < rows.Length; i++)
        {
            var isHovered = Raylib.CheckCollisionPointRec(mousePos, rows[i]);
            if (isHovered)
            {
                Raylib.DrawRectangleRec(rows[i], new Color(62, 62, 62, 255));
            }

            var value = _bindingCaptureIndex == i
                ? "Press key..."
                : FormatKeyName(_playerOneBindings[i]);
            Raylib.DrawText(NesButtonLabels[i], (int)rows[i].X + 8, (int)rows[i].Y + 6, 16, Color.RayWhite);
            Raylib.DrawText(value, (int)rows[i].X + 150, (int)rows[i].Y + 6, 16, new Color(190, 230, 255, 255));
        }

        DrawPanelButton("Reset defaults", resetRect, mousePos);
        DrawPanelButton("Close", closeRect, mousePos);
    }

    private void DrawPanelButton(string label, Rectangle rect, Vector2 mousePos)
    {
        var isHovered = Raylib.CheckCollisionPointRec(mousePos, rect);
        Raylib.DrawRectangleRec(rect, isHovered ? new Color(75, 75, 75, 255) : new Color(50, 50, 50, 255));
        Raylib.DrawRectangleLinesEx(rect, 1, new Color(180, 180, 180, 255));
        Raylib.DrawText(label, (int)rect.X + 10, (int)rect.Y + 5, 16, Color.RayWhite);
    }

    private void HandleKeyBindPanelInput()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Escape))
        {
            _bindingCaptureIndex = -1;
            _keyBindPanelOpen = false;
            return;
        }

        if (_bindingCaptureIndex >= 0)
        {
            var keyPressed = Raylib.GetKeyPressed();
            if (keyPressed > 0)
            {
                SetBinding(_bindingCaptureIndex, (KeyboardKey)keyPressed);
                _bindingCaptureIndex = -1;
            }

            return;
        }

        if (!Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            return;
        }

        var mousePos = Raylib.GetMousePosition();
        BuildKeyBindPanelLayout(out var panelRect, out var rows, out var resetRect, out var closeRect);
        if (!Raylib.CheckCollisionPointRec(mousePos, panelRect))
        {
            _keyBindPanelOpen = false;
            return;
        }

        for (var i = 0; i < rows.Length; i++)
        {
            if (Raylib.CheckCollisionPointRec(mousePos, rows[i]))
            {
                _bindingCaptureIndex = i;
                return;
            }
        }

        if (Raylib.CheckCollisionPointRec(mousePos, resetRect))
        {
            var defaults = CreateDefaultBindings();
            Array.Copy(defaults, _playerOneBindings, _playerOneBindings.Length);
            return;
        }

        if (Raylib.CheckCollisionPointRec(mousePos, closeRect))
        {
            _keyBindPanelOpen = false;
        }
    }

    private void BuildKeyBindPanelLayout(out Rectangle panelRect, out Rectangle[] rows, out Rectangle resetRect, out Rectangle closeRect)
    {
        var rowCount = _playerOneBindings.Length;
        var panelHeight = KeyBindPanelPadding * 2 + KeyBindHeaderHeight + rowCount * KeyBindPanelRowHeight + 44;
        var x = (Raylib.GetScreenWidth() - KeyBindPanelWidth) / 2f;
        var y = (Raylib.GetScreenHeight() - panelHeight) / 2f;

        panelRect = new Rectangle(x, y, KeyBindPanelWidth, panelHeight);
        rows = new Rectangle[rowCount];
        var rowY = y + KeyBindPanelPadding + KeyBindHeaderHeight;
        for (var i = 0; i < rowCount; i++)
        {
            rows[i] = new Rectangle(x + KeyBindPanelPadding, rowY + i * KeyBindPanelRowHeight, KeyBindPanelWidth - KeyBindPanelPadding * 2, KeyBindPanelRowHeight);
        }

        var buttonY = panelRect.Y + panelRect.Height - 36;
        resetRect = new Rectangle(panelRect.X + KeyBindPanelPadding, buttonY, 140, 26);
        closeRect = new Rectangle(panelRect.X + panelRect.Width - KeyBindPanelPadding - 90, buttonY, 90, 26);
    }

    private void SetBinding(int index, KeyboardKey key)
    {
        if (index < 0 || index >= _playerOneBindings.Length)
        {
            return;
        }

        for (var i = 0; i < _playerOneBindings.Length; i++)
        {
            if (i != index && _playerOneBindings[i] == key)
            {
                _playerOneBindings[i] = _playerOneBindings[index];
                break;
            }
        }

        _playerOneBindings[index] = key;
    }

    private static KeyboardKey[] CreateDefaultBindings()
    {
        return
        [
            KeyboardKey.Z,
            KeyboardKey.X,
            KeyboardKey.RightShift,
            KeyboardKey.Enter,
            KeyboardKey.Up,
            KeyboardKey.Down,
            KeyboardKey.Left,
            KeyboardKey.Right
        ];
    }

    private static string FormatKeyName(KeyboardKey key)
    {
        return key switch
        {
            KeyboardKey.RightShift => "Right Shift",
            KeyboardKey.LeftShift => "Left Shift",
            KeyboardKey.Enter => "Enter",
            _ => key.ToString()
        };
    }

    private static string? ShowOpenRomDialog()
    {
        string? romPath = null;
        Exception? error = null;

        var dialogThread = new Thread(() =>
        {
            try
            {
                using var dialog = new System.Windows.Forms.OpenFileDialog
                {
                    Title = "Load ROM",
                    Filter = "NES ROM (*.nes)|*.nes|All files (*.*)|*.*",
                    CheckFileExists = true,
                    CheckPathExists = true,
                    Multiselect = false
                };

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    romPath = dialog.FileName;
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        dialogThread.SetApartmentState(ApartmentState.STA);
        dialogThread.Start();
        dialogThread.Join();

        if (error is not null)
        {
            throw error;
        }

        return romPath;
    }
}
