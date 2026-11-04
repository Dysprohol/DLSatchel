using System.Data;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Modules
{
    /// <summary>
    /// Read-only view of Player.Tools: the tools and the held accessories (umbrellas, parasols…),
    /// each { ToolItemID, CurrentOfType }. New entries are added unequipped by ItemPlacement.AddTool.
    /// </summary>
    public static class ToolModule
    {
        public static DataTable MapTools(JObject root)
        {
            var table = new DataTable("Tools");
            table.Columns.Add("Item ID", typeof(long));
            table.Columns.Add("Name", typeof(string));
            table.Columns.Add("In-game Name", typeof(string));
            table.Columns.Add("Kind", typeof(string));
            table.Columns.Add("In use", typeof(string));

            if (root["Player"]?["Tools"] is not JArray tools)
                return table;

            foreach (var tool in tools.OfType<JObject>())
            {
                long id = tool["ToolItemID"]?.Value<long>() ?? 0;
                if (id <= 0) continue;

                var item = ItemDatabase.Find(id);
                string name = ItemDatabase.GetInternalName(item);
                bool accessory = name.StartsWith("Accessory/", StringComparison.Ordinal) ||
                                 ItemDatabase.Get(item, "CATEGORY", "") == "Accessory";

                table.Rows.Add(
                    id,
                    name,
                    item == null ? "(not in catalog)" : ItemDatabase.Get(item, "LOCALIZED_DISPLAY_NAME"),
                    accessory ? "Accessory" : "Tool",
                    tool["CurrentOfType"]?.Value<bool>() == true ? "Yes" : "No");
            }

            return table;
        }
    }
}
