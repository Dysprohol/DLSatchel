using System;
using System.Collections.Generic;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>
    /// Single entry point to item data. Implementations must be thread-safe:
    /// a reload swaps an immutable snapshot atomically.
    /// </summary>
    public interface IItemCatalog
    {
        int Count { get; }

        /// <summary>All records ordered by id (read-only snapshot).</summary>
        IReadOnlyList<ItemRecord> All { get; }

        ItemRecord? Get(long id);

        bool TryGet(long id, out ItemRecord record);

        IReadOnlyList<ItemRecord> Query(ItemQuery query);

        IReadOnlyList<ItemRecord> ByKind(ItemKind kind);

        IReadOnlyList<ItemRecord> Search(string text, int limit = 200);

        CatalogDiagnostics Diagnostics { get; }

        event EventHandler? Reloaded;
    }
}
