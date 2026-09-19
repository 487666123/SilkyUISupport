using System;
using System.Collections.Immutable;
using System.Threading;

namespace SilkyUISupport;

/// <summary>Keeps one completed analysis for a document/metadata snapshot pair.</summary>
internal sealed class SilkyUIAnalysisCache<T>(Func<SilkyUISemanticModel, ImmutableArray<T>> analyze)
{
    private readonly Func<SilkyUISemanticModel, ImmutableArray<T>> _analyze = analyze;
    private CacheEntry _entry;

    public ImmutableArray<T> GetResults(SilkyUIXmlDocument document, SilkyUIMetadataSnapshot metadata)
    {
        var entry = Volatile.Read(ref _entry);
        if (entry != null && ReferenceEquals(entry.Document, document) && ReferenceEquals(entry.Metadata, metadata))
            return entry.Results;

        var results = _analyze(new SilkyUISemanticModel(document, metadata));
        // Concurrent misses may compute independently; publish only complete, immutable results.
        Volatile.Write(ref _entry, new CacheEntry(document, metadata, results));
        return results;
    }

    private sealed record CacheEntry(
        SilkyUIXmlDocument Document, SilkyUIMetadataSnapshot Metadata, ImmutableArray<T> Results);
}
