using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace DDV_SaveEditor_G.Modules
{
    internal static class SaveDecoder
    {
        /* ───────── stały klucz AES (community) ───────── */
        private static readonly byte[] Key =
            Encoding.ASCII.GetBytes("b5qhh8saJ8UlDJUzTZXd2Tg6mbo8W8n5");

        /// <summary>
        /// True, jeśli ostatnio wczytany plik był zaszyfrowany.
        /// Używane przez Save(), żeby zapisać w tym samym formacie.
        /// </summary>
        internal static bool LastLoadWasEncrypted { get; private set; }

        /* ---------- zapisuje odszyfrowaną kopię obok oryginału --- */
        private static string WriteDecodedCopy(string blobPath, string jsonTxt)
        {
            string dst = Path.Combine(
                Path.GetDirectoryName(blobPath)!,
               $"{Path.GetFileNameWithoutExtension(blobPath)}_decoded.json");

            File.WriteAllText(dst, jsonTxt);
            return dst;
        }

        /* =================================================================
         *  TryLoad
         *  •  .bak  – tworzy zawsze (przy pierwszym odczycie pliku)
         *  •  plain-JSON  → json wczytany, decodedPath = oryginał
         *  •  zaszyfrowany → odszyfrowuje, zapisuje *_decoded.json*
         * ================================================================= */
        internal static bool TryLoad(string originalPath,
                                     out string decodedPath,
                                     out JObject json)
        {
            decodedPath = originalPath;
            LastLoadWasEncrypted = false;

            /* 0 ▪ zawsze kopia .bak ------------------------------------ */
            string bak = $"{originalPath}.bak";
            if (!File.Exists(bak))
                File.Copy(originalPath, bak, overwrite: false);

            if (!TryDecode(originalPath, out json, out bool encrypted, out string jsonTxt))
                return false;

            if (encrypted)
            {
                decodedPath = WriteDecodedCopy(originalPath, jsonTxt);
                LastLoadWasEncrypted = true;
            }
            return true;
        }

        /* =================================================================
         *  TryDecode – sam odczyt, bez żadnych plików ubocznych
         *  (używane też przez SaveAutoDetect do podglądu Name/Level).
         * ================================================================= */
        internal static bool TryDecode(string path, out JObject json, out bool encrypted)
            => TryDecode(path, out json, out encrypted, out _);

        private static bool TryDecode(string path, out JObject json, out bool encrypted, out string jsonTxt)
        {
            json = null!;
            encrypted = false;
            jsonTxt = "";

            /* 1 ▪ plain JSON ------------------------------------------ */
            try
            {
                jsonTxt = File.ReadAllText(path);
                json = JObject.Parse(jsonTxt);
                return true;
            }
            catch
            {
                /* nie-JSON – próbujemy odszyfrować */
            }

            /* 2 ▪ AES-ECB + ZIP / zlib -------------------------------- */
            byte[] blob = File.ReadAllBytes(path);
            encrypted = true;

            for (int skip = 0; skip < 32 && skip < blob.Length; skip++)
            {
                int len = blob.Length - skip;
                if (len % 16 != 0) continue;       // blok 16 B – ECB/NoPadding

                byte[] slice;
                try { slice = AesEcb(blob.AsSpan(skip, len).ToArray(), decrypt: true); }
                catch { continue; }

                /* 2a ▪ ZIP (PK 03 04) -------------------------------- */
                if (slice.Length > 4 &&
                    slice[0] == 0x50 && slice[1] == 0x4B &&
                    slice[2] == 0x03 && slice[3] == 0x04)
                {
                    try
                    {
                        using var ms = new MemoryStream(slice);
                        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                        var entry = zip.Entries.FirstOrDefault();
                        if (entry == null) continue;

                        using var es = entry.Open();
                        using var sr = new StreamReader(es, Encoding.UTF8);
                        jsonTxt = sr.ReadToEnd();
                        json = JObject.Parse(jsonTxt);
                        return true;
                    }
                    catch { continue; }
                }

                /* 2b ▪ Deflate (zlib 78 01 / 78 9C / 78 DA) ---------- */
                for (int i = 0; i < slice.Length - 1; i++)
                {
                    byte b0 = slice[i], b1 = slice[i + 1];
                    if (b0 == 0x78 && (b1 == 0x01 || b1 == 0x9C || b1 == 0xDA))
                    {
                        try
                        {
                            jsonTxt = InflateToString(slice, i + 2); // +2: pomijamy nagłówek zlib
                            json = JObject.Parse(jsonTxt);
                            return true;
                        }
                        catch { }
                    }
                }
            }

            /* 3 ▪ nie rozpoznano formatu ----------------------------- */
            json = null!;
            encrypted = false;
            jsonTxt = "";
            return false;
        }

        /* =================================================================
         *  Save – zapisuje JSON z powrotem na dysk.
         *  Jeśli oryginał był zaszyfrowany → koduje symetrycznie:
         *  JSON → ZIP (wpis "profile", Deflate) → PKCS7 pad → AES-256-ECB.
         *  Format zweryfikowany na realnym sejwie (padding PKCS7 za EOCD).
         * ================================================================= */
        internal static string? Save(string originalPath, JObject json)
        {
            // Keep the original first-load .bak as an untouched anchor and create a
            // fresh sibling backup before every overwrite, so later saves are reversible too.
            string? backupPath = File.Exists(originalPath)
                ? CreateTimestampedBackup(originalPath)
                : null;

            string jsonTxt = json.ToString(Newtonsoft.Json.Formatting.None);
            byte[] contents = LastLoadWasEncrypted
                ? Encode(jsonTxt)
                : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(jsonTxt);

            WriteAtomically(originalPath, contents, json, LastLoadWasEncrypted);
            return backupPath;
        }

        private static string CreateTimestampedBackup(string originalPath)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            for (int suffix = 0; ; suffix++)
            {
                string extra = suffix == 0 ? "" : $"-{suffix}";
                string backupPath = $"{originalPath}.backup-{stamp}{extra}";
                try
                {
                    File.Copy(originalPath, backupPath, overwrite: false);
                    return backupPath;
                }
                catch (IOException) when (File.Exists(backupPath))
                {
                    // A second save in the same millisecond gets a unique sibling name.
                }
            }
        }

        /// <summary>
        /// Writes and flushes a complete sibling file before replacing the save. This keeps
        /// an interrupted write from leaving a truncated profile at the original path.
        /// </summary>
        private static void WriteAtomically(string destinationPath, byte[] contents,
                                            JObject expectedJson, bool expectedEncrypted)
        {
            string fullPath = Path.GetFullPath(destinationPath);
            string directory = Path.GetDirectoryName(fullPath)!;
            string tempPath = Path.Combine(directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                                                   FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                {
                    stream.Write(contents, 0, contents.Length);
                    stream.Flush(flushToDisk: true);
                }

                // Validate the complete sibling file before it can replace the user's save.
                if (!TryDecode(tempPath, out var verifiedJson, out bool verifiedEncrypted) ||
                    verifiedEncrypted != expectedEncrypted ||
                    !JToken.DeepEquals(expectedJson, verifiedJson))
                {
                    throw new InvalidDataException("The staged save failed JSON/encryption round-trip validation.");
                }

                if (File.Exists(fullPath))
                {
                    try
                    {
                        File.Replace(tempPath, fullPath, destinationBackupFileName: null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        // Both files are siblings on the same volume, so replacement remains a rename.
                        File.Move(tempPath, fullPath, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tempPath, fullPath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        /// <summary>JSON → zaszyfrowany blob w formacie gry.</summary>
        internal static byte[] Encode(string jsonTxt)
        {
            byte[] zipBytes;
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = zip.CreateEntry("profile", CompressionLevel.Optimal);
                    using var es = entry.Open();
                    byte[] payload = Encoding.UTF8.GetBytes(jsonTxt);
                    es.Write(payload, 0, payload.Length);
                }
                zipBytes = ms.ToArray();
            }

            // PKCS7 + AES-256-ECB (Aes z PaddingMode.PKCS7 robi to samo)
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;
            using var enc = aes.CreateEncryptor();
            return enc.TransformFinalBlock(zipBytes, 0, zipBytes.Length);
        }

        /* =============== helpers =================================== */

        private static byte[] AesEcb(byte[] data, bool decrypt)
        {
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;

            using var tr = decrypt ? aes.CreateDecryptor() : aes.CreateEncryptor();
            return tr.TransformFinalBlock(data, 0, data.Length);
        }

        private static string InflateToString(byte[] buf, int offset)
        {
            using var msIn = new MemoryStream(buf, offset, buf.Length - offset);
            using var zIn = new DeflateStream(msIn, CompressionMode.Decompress);
            using var sr = new StreamReader(zIn, Encoding.UTF8);
            return sr.ReadToEnd();
        }
    }
}
