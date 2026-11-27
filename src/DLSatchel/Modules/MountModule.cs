using System.Data;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Modules
{
    /// <summary>Read-only projection of the game's Player.Mounts progression objects.</summary>
    public static class MountModule
    {
        /// <summary>
        /// Adds a mount as a deliberately minimal experimental record. Existing mount records
        /// are never cloned because they contain player-specific bond, upgrade, and customization data.
        /// </summary>
        public static bool AddMount(JObject root, long id)
        {
            if (id <= 0 || root["Player"] is not JObject player || player["Mounts"] is not JArray mounts)
                return false;

            if (mounts.OfType<JObject>().Any(m => m["MountItemID"]?.Value<long>() == id))
                return false;

            mounts.Add(new JObject { ["MountItemID"] = id });
            return true;
        }

        public static DataTable MapMounts(JObject root)
        {
            var table = new DataTable("Mounts");
            table.Columns.Add("Mount ID", typeof(long));
            table.Columns.Add("Name", typeof(string));
            table.Columns.Add("Bond level", typeof(string));
            table.Columns.Add("Bond points", typeof(string));
            table.Columns.Add("Ability level", typeof(string));
            table.Columns.Add("Customized", typeof(string));

            if (root["Player"]?["Mounts"] is not JArray mounts)
                return table;

            foreach (JToken? mount in mounts)
            {
                long id = mount?["MountItemID"]?.Value<long>() ?? 0;
                if (id <= 0)
                    continue;

                var item = ItemDatabase.Find(id);
                string name = item == null
                    ? id.ToString()
                    : ItemDatabase.Get(item, "LOCALIZED_DISPLAY_NAME",
                        ItemDatabase.Get(item, "NAME", id.ToString()));

                table.Rows.Add(
                    id,
                    name,
                    Display(mount?["BondingLevel"]),
                    Display(mount?["BondingPoints"]),
                    DisplayAbilities(mount?["ValleyAbilityForLevel"]),
                    mount?["Customization"] is JObject ? "Yes" : "No");
            }

            return table;
        }

        /// <summary>"Digging, Mining" instead of the raw { "2": id, "3": id } (level: ability) the save keeps.</summary>
        private static string DisplayAbilities(JToken? value)
        {
            if (value is not JObject levels || !levels.HasValues) return Display(value);
            return string.Join(", ", levels.Properties().Select(level =>
            {
                long id = level.Value.Type == JTokenType.Integer ? level.Value.Value<long>() : 0;
                var item = id > 0 ? ItemDatabase.Find(id) : null;
                string name = item == null ? level.Value.ToString() : ItemDatabase.Get(item, "LOCALIZED_DISPLAY_NAME", ItemDatabase.Get(item, "NAME", id.ToString()));
                return name;
            }));
        }

        private static string Display(JToken? value) =>
            value == null || value.Type == JTokenType.Null ? "—" : value.ToString();
    }
}
