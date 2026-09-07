using System;

namespace MadTOM.Services;

public interface INotificationService
{
    void ShowToast(string message, string icon = "ℹ️");
    event EventHandler<(string Message, string Icon)>? ToastRequested;
}

public sealed class NotificationService : INotificationService
{
    private static readonly Lazy<NotificationService> _lazyInstance = new(() => new NotificationService());
    public static NotificationService Instance => _lazyInstance.Value;

    public event EventHandler<(string Message, string Icon)>? ToastRequested;

    public void ShowToast(string message, string icon = "ℹ️")
    {
        ToastRequested?.Invoke(this, (message, icon));
    }
}

