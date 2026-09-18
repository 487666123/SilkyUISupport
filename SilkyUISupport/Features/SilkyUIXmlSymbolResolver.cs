using Microsoft.VisualStudio.Text;

namespace SilkyUISupport;

/// <summary>VS 适配器的兼容性入口；语义规则在 SilkyUISemanticModel 中。</summary>
internal static class SilkyUIXmlSymbolResolver
{
    public static bool TryResolve(
        ITextSnapshot snapshot,
        int position,
        SilkyUIMetadataService metadataService,
        out SilkyUISymbolInfo resolution)
    {
        resolution = null;
        if (snapshot == null || metadataService == null || position < 0 || position >= snapshot.Length)
            return false;

        var model = new SilkyUISemanticModel(
            SilkyUIXmlDocument.Get(snapshot), metadataService.GetSnapshot());
        return model.TryResolveSymbol(position, out resolution);
    }
}
