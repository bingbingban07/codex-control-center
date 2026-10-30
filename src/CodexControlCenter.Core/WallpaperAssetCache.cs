namespace CodexControlCenter.Core;

/// <summary>
/// Bounded process wide cache for the lightweight wallpaper descriptors used by the library list.
/// The cache never stores image bytes: an entry is only reused while the metadata file and the
/// managed image keep the same length and the same modification time, so a renamed, replaced or
/// deleted entry is always refreshed or dropped instead of being served from memory.
/// </summary>
internal sealed class WallpaperAssetCache
{
    /// <summary>The hard upper bound of the cache, the library list stays responsive beyond it.</summary>
    internal const int MaximumEntries = 256;

    internal sealed record Entry(
        WallpaperAsset Asset,
        long MetadataLength,
        long MetadataModifiedTicks,
        long ImageLength,
        long ImageModifiedTicks);

    private const char KeySeparator = '\u0000';

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, LinkedListNode<string>> Recency = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Order = new();

    public static bool TryGet(string root, string id, out Entry entry)
    {
        string key = BuildKey(root, id);
        lock (Sync)
        {
            if (!Entries.TryGetValue(key, out Entry? found))
            {
                entry = null!;
                return false;
            }

            Touch(key);
            entry = found;
            return true;
        }
    }

    public static void Set(string root, string id, Entry entry)
    {
        string key = BuildKey(root, id);
        lock (Sync)
        {
            Entries[key] = entry;
            Touch(key);
            while (Entries.Count > MaximumEntries)
            {
                LinkedListNode<string>? oldest = Order.Last;
                if (oldest is null)
                {
                    break;
                }

                Order.RemoveLast();
                Recency.Remove(oldest.Value);
                Entries.Remove(oldest.Value);
            }
        }
    }

    public static void Invalidate(string root, string id)
    {
        string key = BuildKey(root, id);
        lock (Sync)
        {
            Entries.Remove(key);
            if (Recency.TryGetValue(key, out LinkedListNode<string>? node))
            {
                Order.Remove(node);
                Recency.Remove(key);
            }
        }
    }

    /// <summary>Drops the cached descriptors of entries that no longer exist on disk.</summary>
    public static void Prune(string root, IReadOnlySet<string> liveIds)
    {
        string prefix = BuildKey(root, string.Empty);
        lock (Sync)
        {
            List<string>? stale = null;
            foreach (string key in Entries.Keys)
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string id = key[prefix.Length..];
                if (!liveIds.Contains(id))
                {
                    (stale ??= new List<string>()).Add(key);
                }
            }

            if (stale is null)
            {
                return;
            }

            foreach (string key in stale)
            {
                Entries.Remove(key);
                if (Recency.TryGetValue(key, out LinkedListNode<string>? node))
                {
                    Order.Remove(node);
                    Recency.Remove(key);
                }
            }
        }
    }

    private static void Touch(string key)
    {
        if (Recency.TryGetValue(key, out LinkedListNode<string>? node))
        {
            Order.Remove(node);
            Recency.Remove(key);
        }

        Recency[key] = Order.AddFirst(key);
    }

    private static string BuildKey(string root, string id) =>
        root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + KeySeparator + id;
}
