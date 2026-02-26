namespace cunes.Frontend;

public interface IRenderer : IDisposable
{
    bool IsInteractive { get; }
    bool ShouldClose { get; }

    void DrawFrame(byte[] frameBuffer);
    void SubmitAudioSamples(float[] samples, int sampleCount);
    void SetWindowTitle(string title);

    byte GetControllerState(int player);
    bool TryDequeueUiAction(out UiAction action);
    void SetRomLoaded(bool isLoaded);
}
