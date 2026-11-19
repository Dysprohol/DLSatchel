using System.Data;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Modules
{
    /// <summary>
    /// Read-only view of the unlocked Gameloft decoration presets:
    /// Player.PremadeDecorationPresets = { DecorationPresets: [id…], Favorites: [id…] }.
    /// New ones are added by ItemPlacement.AddPreset.
    /// </summary>
    public static class PresetModule
    {
        public static DataTable MapPresets(JObject root)
        {
            var table = new DataTable("Presets");
            table.Columns.Add("Preset ID", typeof(long));
            table.Columns.Add("Name", typeof(string));
            table.Columns.Add("Furniture", typeof(string));
            table.Columns.Add("Favorite", typeof(string));

            if (ItemPlacement.PresetList(root) is not JArray presets)
                return table;

            var favorites = (root["Player"]?["PremadeDecorationPresets"]?["Favorites"] as JArray)?
                .Where(f => f.Type == JTokenType.Integer).Select(f => (long)f).ToHashSet() ?? new HashSet<long>();

            foreach (var entry in presets.Where(p => p.Type == JTokenType.Integer))
            {
                long id = (long)entry;
                var item = ItemDatabase.Find(id);
                var layout = PresetLayouts.Get(id);
                table.Rows.Add(
                    id,
                    item == null ? "(not in catalog)" : ItemDatabase.Get(item, "LOCALIZED_DISPLAY_NAME"),
                    layout == null ? "?" : $"{layout.Pieces} pieces",
                    favorites.Contains(id) ? "Yes" : "No");
            }

            return table;
        }
    }
}
