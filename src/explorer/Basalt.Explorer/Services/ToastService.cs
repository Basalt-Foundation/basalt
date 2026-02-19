namespace Basalt.Explorer.Services;

public sealed class ToastService
{
    public event Action<ToastMessage>? OnToast;

    public void Show(string message, ToastType type = ToastType.Info, int durationMs = 5000)
    {
        OnToast?.Invoke(new ToastMessage(message, type, durationMs));
    }

    public void Success(string message) => Show(message, ToastType.Success);
    public void Error(string message) => Show(message, ToastType.Error);
    public void Warning(string message) => Show(message, ToastType.Warning);
    public void Info(string message) => Show(message, ToastType.Info);
}

public sealed record ToastMessage(string Message, ToastType Type, int DurationMs);

public enum ToastType { Info, Success, Warning, Error }
