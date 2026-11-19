using System.Collections.Generic;

namespace DDV_SaveEditor_G.Data
{
    /// <summary>
    /// Composable filter for <see cref="IItemCatalog.Query"/>. Immutable, so it can
    /// be handed to another thread without copying.
    /// </summary>
    public sealed record ItemQuery
    {
        /// <summary>Free text; matched against display/internal name, id and subtype.</summary>
        public string? Text { get; init; }

        public ItemKind? Kind { get; init; }

        public string? Subtype { get; init; }

        public string? Patch { get; init; }

        /// <summary>Only items whose safety is Safe (or Caution? no – only Safe).</summary>
        public bool SafeOnly { get; init; }

        /// <summary>When false, Unsafe records are filtered out (default is to show them).</summary>
        public bool IncludeUnsafe { get; init; } = true;

        /// <summary>When set, only these ids are returned ("owned only").</summary>
        public IReadOnlyCollection<long>? Ids { get; init; }

        public int Skip { get; init; }

        public int Take { get; init; } = 200;
    }
}
