using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Modules
{
    /// <summary>
    /// Parses save JSON without turning date strings into DateTime. Newtonsoft would
    /// otherwise write "…07.133760Z" back as "…07.13376Z"; kept as text, every value the
    /// editor does not change is written back exactly as the game wrote it.
    /// </summary>
    internal static class SaveJson
    {
        public static JToken Parse(string json)
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            var token = JToken.ReadFrom(reader);
            while (reader.Read())
                if (reader.TokenType != JsonToken.Comment)
                    throw new JsonReaderException("Additional text found after the JSON.");
            return token;
        }

        public static JObject ParseObject(string json) =>
            Parse(json) as JObject ?? throw new JsonReaderException("The JSON is not an object.");
    }
}
