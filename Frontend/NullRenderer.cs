namespace cunes.Frontend;

public sealed class NullRenderer : IRenderer
{
    public bool IsInteractive => false;

    public bool ShouldClose => false;

    public void DrawFrame(byte[] frameBuffer)
    {
        _ = frameBuffer;
    }

    public void SubmitAudioSamples(float[] samples, int sampleCount)
    {
        _ = samples;
        _ = sampleCount;
    }

    public byte GetControllerState(int player)
    {
        _ = player;
        return 0;
    }

    public bool TryDequeueUiAction(out UiAction action)
    {
        action = default;
        return false;
    }

    public void SetRomLoaded(bool isLoaded)
    {
        _ = isLoaded;
    }

    public void Dispose()
    {
    }
}
