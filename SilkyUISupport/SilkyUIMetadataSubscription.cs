using System;

namespace SilkyUISupport;

/// <summary>Keep the shared metadata service from retaining closed XML buffers.</summary>
internal static class SilkyUIMetadataSubscription
{
    // Callers must pass a static callback so it cannot capture the subscriber.
    public static void Subscribe<T>(SilkyUIMetadataService service, T subscriber, Action<T> onRefreshed)
        where T : class
        => _ = new Subscription<T>(service, subscriber, onRefreshed);

    private sealed class Subscription<T> where T : class
    {
        private readonly SilkyUIMetadataService _service;
        private readonly WeakReference<T> _subscriber;
        private readonly Action<T> _onRefreshed;

        public Subscription(SilkyUIMetadataService service, T subscriber, Action<T> onRefreshed)
        {
            _service = service;
            _subscriber = new WeakReference<T>(subscriber);
            _onRefreshed = onRefreshed;
            _service.Refreshed += OnRefreshed;
        }

        private void OnRefreshed()
        {
            if (_subscriber.TryGetTarget(out var subscriber))
                _onRefreshed(subscriber);
            else
                _service.Refreshed -= OnRefreshed;
        }
    }
}
