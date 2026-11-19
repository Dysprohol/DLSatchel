using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Modules
{
    public static class PlayerModule
    {
        /// <summary>
        /// Map player data (name, level, currencies) from JSON to a structure.
        /// </summary>
        /// <param name="jsonData">The JSON object containing player data.</param>
        /// <returns>A dictionary with player data, or null if data is missing.</returns>
        public static Dictionary<string, string> MapPlayerData(JObject jsonData)
        {
            if (jsonData == null || !jsonData.ContainsKey("Player"))
            {
                return null;
            }

            var playerData = new Dictionary<string, string>();
            var player = jsonData["Player"] as JObject;

            playerData["Name"] = player?["Name"]?.ToString() ?? "Unknown";
            playerData["Level"] = player?["Level"]?.ToString() ?? "0";
            playerData["Xp"] = player?["Xp"]?.ToString() ?? "0";

            var currency = player?["CurrencyAmounts"] as JObject;
            if (currency != null)
            {
                playerData["Dreamlight"] = currency["80300000"]?.ToString() ?? "0";
                playerData["StarCoins"] = currency["80000000"]?.ToString() ?? "0";
                playerData["PixelDust"] = currency["80200002"]?.ToString() ?? "0";
                playerData["Mist"] = currency["80000003"]?.ToString() ?? "0";
                playerData["DaisyCoins"] = currency["80000009"]?.ToString() ?? "0";
                playerData["StorybookMagic"] = currency["80000010"]?.ToString() ?? "0";
            }

            return playerData;
        }

        /// <summary>
        /// Save player data back to JSON.
        /// </summary>
        /// <param name="jsonData">The JSON object to update.</param>
        /// <param name="playerData">The updated player data to save.</param>
        public static void SavePlayerData(JObject jsonData, Dictionary<string, string> playerData)
        {
            if (jsonData == null || !jsonData.ContainsKey("Player"))
            {
                return;
            }

            var player = jsonData["Player"] as JObject;

            if (playerData.ContainsKey("Name"))
                player["Name"] = playerData["Name"];

            if (playerData.ContainsKey("Level") && int.TryParse(playerData["Level"], out int level))
                player["Level"] = level;

            if (playerData.ContainsKey("Xp") && int.TryParse(playerData["Xp"], out int xp))
                player["Xp"] = xp;

            var currency = player["CurrencyAmounts"] as JObject;
            if (currency != null)
            {
                SetCurrency(currency, "80300000", playerData, "Dreamlight");
                SetCurrency(currency, "80000000", playerData, "StarCoins");
                SetCurrency(currency, "80200002", playerData, "PixelDust");
                SetCurrency(currency, "80000003", playerData, "Mist");
                SetCurrency(currency, "80000009", playerData, "DaisyCoins");
                SetCurrency(currency, "80000010", playerData, "StorybookMagic");
            }
        }

        /// <summary>
        /// Nowsze sejwy mają pusty słownik CurrencyAmounts ({}), gdy gracz nie ma danej waluty –
        /// nie dopisujemy zer, tylko istniejące klucze albo niezerowe wartości.
        /// </summary>
        private static void SetCurrency(JObject currency, string currencyId, Dictionary<string, string> playerData, string field)
        {
            if (!playerData.TryGetValue(field, out var txt) || !int.TryParse(txt, out int value))
                return;

            if (currency.ContainsKey(currencyId) || value != 0)
                currency[currencyId] = value;
        }
    }
}
