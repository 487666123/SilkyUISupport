using System;

namespace SilkyUISupport;

/// <summary>防止共享元数据服务保留已关闭的 XML 缓冲区。</summary>
internal static class SilkyUIMetadataSubscription
{
    // 调用者必须传递静态回调，确保不捕获订阅者。
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
