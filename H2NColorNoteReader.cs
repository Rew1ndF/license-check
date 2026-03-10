using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace WinHK3
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  TrackerVersion — версия Hand2Note
    // ═══════════════════════════════════════════════════════════════════════════
    public enum TrackerVersion { H2N3, H2N4 }

    // ═══════════════════════════════════════════════════════════════════════════
    //  H2NColorNoteReader
    //
    //  Архитектура (упрощённая и надёжная):
    //
    //  1. FishMarkers — список активных приписок (Description), которые считаются
    //     «рыбой» или «регом». Id маркера = Description из H2N (например «nit», «fish»).
    //
    //  2. _nickCache — кэш ник → MarkerResult (описание + цвет).
    //     Заполняется при первом обращении к нику путём чтения .cm или .colormarker файла.
    //
    //  3. Поиск файла игрока:
    //     а) По шаблону «<nick>_*.cm» / «<nick>_*.colormarker» — быстро
    //     б) Полный скан (до 1000 файлов) — если быстрый не нашёл
    //     в) PreloadAll() — сканирует все файлы при старте
    //
    //  Совместимость: FishMarker.Id может содержать как Description («nit»), так и
    //  GUID (старый формат). При матчинге сначала проверяется Description файла,
    //  затем GUID из .cm JSON — поддержка обоих форматов одновременно.
    // ═══════════════════════════════════════════════════════════════════════════
    public class H2NColorNoteReader
    {
        // ── Маркер (приписка) ────────────────────────────────────────────────
        public class FishMarkerEntry
        {
            public string Id { get; set; } = "";   // Description или GUID
            public bool Active { get; set; } = true;
            public string Color { get; set; } = "#FF9602";
            public bool Permanent { get; set; } = false;

            public FishMarkerEntry() { }
            public FishMarkerEntry(string id, bool active, string color, bool permanent = false)
            { Id = id; Active = active; Color = color; Permanent = permanent; }

            public JObject ToJson() => new JObject
            { ["id"] = Id, ["active"] = Active, ["color"] = Color, ["permanent"] = Permanent };

            public static FishMarkerEntry FromJson(JToken t) => new FishMarkerEntry
            {
                Id = t["id"]?.Value<string>() ?? "",
                Active = t["active"]?.Value<bool>() ?? true,
                Color = t["color"]?.Value<string>() ?? "#FF9602",
                Permanent = t["permanent"]?.Value<bool>() ?? false
            };
        }

        // ── Результат матчинга ника ──────────────────────────────────────────
        private sealed class MarkerResult
        {
            public string Description; // Description из файла игрока (e.g. "nit")
            public string GuidId;      // GUID из .cm файла (e.g. "536c15c8-...")
            public string FileColor;   // Цвет из файла (#FF9602)
            public DateTime LoadedAt;
        }

        // ── Настройки ────────────────────────────────────────────────────────
        public string ColorMarkersPath { get; set; } = "";
        public readonly List<FishMarkerEntry> FishMarkers = new List<FishMarkerEntry>();

        private readonly object _lock = new object();

        // ── Кэш ников ────────────────────────────────────────────────────────
        // nick → MarkerResult (null если файл не найден / нет маркера)
        private readonly Dictionary<string, MarkerResult> _nickCache
            = new Dictionary<string, MarkerResult>(StringComparer.OrdinalIgnoreCase);

        // Ники, для которых уже делали поиск (даже если не нашли)
        private readonly HashSet<string> _searchedNicks
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private string _lastLoadedPath = "";
        private DateTime _lastFullScan = DateTime.MinValue;

        // ── Публичный API ────────────────────────────────────────────────────

        public void SetFishMarkers(IEnumerable<FishMarkerEntry> entries)
        {
            FishMarkers.Clear();
            FishMarkers.AddRange(entries);
        }

        public JArray MarkersToJson() =>
            new JArray(FishMarkers.Select(m => m.ToJson()));

        public void MarkersFromJson(JArray arr)
        {
            SetFishMarkers(arr?.Select(FishMarkerEntry.FromJson)
                              .Where(e => !string.IsNullOrWhiteSpace(e.Id))
                           ?? Enumerable.Empty<FishMarkerEntry>());
        }

        public void SetFishMarkerIds(IEnumerable<string> ids)
        {
            SetFishMarkers(ids.Where(s => !string.IsNullOrWhiteSpace(s))
                              .Select(s => new FishMarkerEntry(s.Trim(), true, "#FF9602")));
        }

        public void InvalidateCache()
        {
            lock (_lock)
            {
                _nickCache.Clear();
                _searchedNicks.Clear();
                _lastLoadedPath = "";
                _lastFullScan = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Возвращает true, если за никнеймом есть активный маркер из FishMarkers.
        /// </summary>
        public bool IsFish(string nickname)
            => GetMatchResult(nickname) != null;

        /// <summary>
        /// Возвращает цвет маркера для ника (из FishMarkers, с учётом цвета из файла).
        /// null если ник не найден / маркер неактивен.
        /// </summary>
        public string GetFishColor(string nickname)
        {
            var r = GetMatchResult(nickname);
            if (r == null) return null;

            // Цвет берём из FishMarkers (что настроил пользователь),
            // или из файла игрока если маркер не нашли в списке
            var marker = FindActiveMarker(r);
            return marker?.Color ?? r.FileColor;
        }

        /// <summary>
        /// Возвращает Description маркера, которому соответствует ник.
        /// </summary>
        public string GetMatchedMarkerId(string nickname)
        {
            var r = GetMatchResult(nickname);
            if (r == null) return null;
            var marker = FindActiveMarker(r);
            return marker?.Id ?? r.Description ?? r.GuidId;
        }

        /// <summary>
        /// Проверяет, постоянный ли маркер у ника.
        /// </summary>
        public bool IsPermanent(string nickname)
        {
            var r = GetMatchResult(nickname);
            if (r == null) return false;
            return FindActiveMarker(r)?.Permanent ?? false;
        }

        /// <summary>
        /// Предзагружает все файлы маркеров из директории.
        /// </summary>
        public void PreloadAll()
        {
            if (string.IsNullOrEmpty(ColorMarkersPath) || !Directory.Exists(ColorMarkersPath)) return;
            lock (_lock)
            {
                EnsurePathReset();
                try
                {
                    foreach (var f in Directory.GetFiles(ColorMarkersPath, "*.cm"))
                        LoadAndCacheFile(f);
                    foreach (var f in Directory.GetFiles(ColorMarkersPath, "*.colormarker"))
                        LoadAndCacheFile(f);
                    _lastFullScan = DateTime.Now;
                }
                catch { }
            }
        }

        /// <summary>
        /// Возвращает множество ников из переданного списка, которые являются fish.
        /// </summary>
        public HashSet<string> GetFishNicknames(IEnumerable<string> nicknames)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (FishMarkers.Count == 0 || string.IsNullOrEmpty(ColorMarkersPath)) return result;
            foreach (var nick in nicknames)
                if (!string.IsNullOrEmpty(nick) && IsFish(nick))
                    result.Add(nick);
            return result;
        }

        // ── Импорт из конфиг-файла трекера ──────────────────────────────────

        /// <summary>
        /// Парсит файл конфигурации маркеров трекера.
        /// H2N3: ColorMarkers.cg (бинарный protobuf-подобный)
        /// H2N4: ColorMarkerConfig.h2nconfig (бинарный protobuf-подобный)
        /// </summary>
        public static List<FishMarkerEntry> ImportMarkersFromConfigFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return new List<FishMarkerEntry>();
            try
            {
                byte[] data = File.ReadAllBytes(filePath);
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".cg") return ParseH2N3Cg(data);
                if (ext == ".h2nconfig") return ParseH2N4Config(data);
            }
            catch { }
            return new List<FishMarkerEntry>();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  INTERNAL: матчинг ника
        // ═════════════════════════════════════════════════════════════════════

        private MarkerResult GetMatchResult(string nickname)
        {
            if (string.IsNullOrEmpty(nickname)) return null;
            if (FishMarkers.Count == 0) return null;
            if (string.IsNullOrEmpty(ColorMarkersPath) || !Directory.Exists(ColorMarkersPath)) return null;

            lock (_lock)
            {
                EnsurePathReset();

                // Уже есть в кэше (даже null-результат помечен в _searchedNicks)
                if (_nickCache.TryGetValue(nickname, out var cached))
                    return IsStale(cached) ? RefreshNick(nickname) : MatchResult(cached);

                if (_searchedNicks.Contains(nickname))
                    return null; // уже искали — не нашли

                return SearchAndCache(nickname);
            }
        }

        private MarkerResult SearchAndCache(string nickname)
        {
            _searchedNicks.Add(nickname);

            // 1. Быстрый поиск по имени файла
            var result = TryLoadByFilename(nickname);

            // 2. Если не нашли — полный скан (не чаще раза в 30 сек)
            if (result == null && (DateTime.Now - _lastFullScan).TotalSeconds > 30)
            {
                result = FullScanForNick(nickname);
                _lastFullScan = DateTime.Now;
            }

            if (result != null)
            {
                _nickCache[nickname] = result;
                return MatchResult(result);
            }
            return null;
        }

        private MarkerResult RefreshNick(string nickname)
        {
            _nickCache.Remove(nickname);
            _searchedNicks.Remove(nickname);
            return SearchAndCache(nickname);
        }

        /// <summary>
        /// Проверяет активный маркер для данного результата из файла.
        /// Поддерживает ОБА формата Id: Description («nit») и GUID («536c15c8-...»).
        /// </summary>
        private FishMarkerEntry FindActiveMarker(MarkerResult r)
        {
            foreach (var m in FishMarkers)
            {
                if (!m.Active || string.IsNullOrWhiteSpace(m.Id)) continue;
                string mid = m.Id.Trim();

                // Матч по Description (новый формат)
                if (!string.IsNullOrEmpty(r.Description) &&
                    string.Equals(mid, r.Description, StringComparison.OrdinalIgnoreCase))
                    return m;

                // Матч по GUID (старый формат — обратная совместимость)
                if (!string.IsNullOrEmpty(r.GuidId) &&
                    string.Equals(mid, r.GuidId, StringComparison.OrdinalIgnoreCase))
                    return m;
            }
            return null;
        }

        private MarkerResult MatchResult(MarkerResult r)
            => FindActiveMarker(r) != null ? r : null;

        private static bool IsStale(MarkerResult r)
            => r != null && (DateTime.Now - r.LoadedAt).TotalMinutes > 2;

        // ═════════════════════════════════════════════════════════════════════
        //  INTERNAL: загрузка файлов
        // ═════════════════════════════════════════════════════════════════════

        private void EnsurePathReset()
        {
            if (_lastLoadedPath != ColorMarkersPath)
            {
                _nickCache.Clear();
                _searchedNicks.Clear();
                _lastLoadedPath = ColorMarkersPath;
                _lastFullScan = DateTime.MinValue;
            }
        }

        private MarkerResult TryLoadByFilename(string nickname)
        {
            try
            {
                string safe = SanitizeForFilename(nickname);
                var patterns = new[]
                {
                    safe + "_*.cm",
                    safe + ".cm",
                    safe + "_*.colormarker",
                    safe + ".colormarker",
                };
                foreach (var pat in patterns)
                {
                    foreach (var f in Directory.GetFiles(ColorMarkersPath, pat))
                    {
                        var r = ParsePlayerFile(f);
                        if (r != null)
                        {
                            // Регистрируем всех ников из файла в кэше
                            CacheResult(r.Item1, r.Item2);
                            if (_nickCache.TryGetValue(nickname, out var hit)) return hit;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private MarkerResult FullScanForNick(string nickname)
        {
            int scanned = 0;
            foreach (var ext in new[] { "*.cm", "*.colormarker" })
            {
                string[] files;
                try { files = Directory.GetFiles(ColorMarkersPath, ext); }
                catch { continue; }

                foreach (var f in files)
                {
                    if (scanned++ > 5000) break;
                    LoadAndCacheFile(f);
                    if (_nickCache.TryGetValue(nickname, out var hit)) return hit;
                }
                if (scanned > 5000) break;
            }
            return null;
        }

        private void LoadAndCacheFile(string filePath)
        {
            var parsed = ParsePlayerFile(filePath);
            if (parsed != null) CacheResult(parsed.Item1, parsed.Item2);
        }

        private void CacheResult(string nick, MarkerResult result)
        {
            if (!string.IsNullOrEmpty(nick))
            {
                _nickCache[nick] = result;
                _searchedNicks.Add(nick);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Парсинг файлов игроков
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>Парсит файл игрока. Возвращает (nickname, MarkerResult) или null.</summary>
        private static Tuple<string, MarkerResult> ParsePlayerFile(string filePath)
        {
            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".cm") return ParseCmFile(filePath);
                if (ext == ".colormarker") return ParseColormarkerFile(filePath);
            }
            catch { }
            return null;
        }

        // H2N3 JSON: { "Player": {"Nickname": "nick"}, "ColorMarker": {"Id": "guid", "Color": "#...", "Description": "nit"} }
        private static Tuple<string, MarkerResult> ParseCmFile(string filePath)
        {
            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var obj = JObject.Parse(json);
                string nick = obj["Player"]?["Nickname"]?.Value<string>();
                string descr = obj["ColorMarker"]?["Description"]?.Value<string>();
                string guid = obj["ColorMarker"]?["Id"]?.Value<string>();
                string color = obj["ColorMarker"]?["Color"]?.Value<string>();

                if (string.IsNullOrWhiteSpace(nick)) return null;

                return Tuple.Create(nick.Trim(), new MarkerResult
                {
                    Description = descr?.Trim(),
                    GuidId = guid?.Trim(),
                    FileColor = NormalizeColor(color) ?? "#FF9602",
                    LoadedAt = DateTime.Now
                });
            }
            catch { return null; }
        }

        // H2N4 binary: 0c 0e 01 0c 09 <nick> 10 01 14 <outer_len> 0a <id_bytes> 14 <desc_len> <desc> 1c <color_len> <color> 20 01
        private static Tuple<string, MarkerResult> ParseColormarkerFile(string filePath)
        {
            try
            {
                byte[] data = File.ReadAllBytes(filePath);
                if (data.Length < 10) return null;

                // Извлекаем ник — ищем паттерн 0c 09 <9-bytes> или 0c <len> <nick>
                string nickname = ExtractNicknameFromColormarker(data);
                if (string.IsNullOrEmpty(nickname)) return null;

                // Ищем описание и цвет: 14 <len> <desc> 1c <len> <color>
                // Структура может быть nested (outer 14 <big_len> { inner 14 <desc> 1c <color> })
                string description = null;
                string color = null;

                for (int i = 0; i < data.Length - 4; i++)
                {
                    if (data[i] != 0x14) continue;
                    int dlen = data[i + 1];
                    if (dlen == 0 || dlen >= 64) continue;
                    if (i + 2 + dlen >= data.Length) continue;
                    if (data[i + 2 + dlen] != 0x1c) continue;

                    string d;
                    try { d = Encoding.UTF8.GetString(data, i + 2, dlen); }
                    catch { continue; }
                    if (!IsValidDescription(d)) continue;

                    int cs = i + 2 + dlen + 1;
                    if (cs >= data.Length) continue;
                    int clen = data[cs];
                    if (clen == 0 || cs + 1 + clen > data.Length) continue;

                    string rawColor;
                    try { rawColor = Encoding.ASCII.GetString(data, cs + 1, clen); }
                    catch { continue; }

                    description = d.Trim();
                    color = NormalizeColor(rawColor);
                    break;
                }

                return Tuple.Create(nickname, new MarkerResult
                {
                    Description = description,
                    GuidId = null,
                    FileColor = color ?? "#FF9602",
                    LoadedAt = DateTime.Now
                });
            }
            catch { return null; }
        }

        private static string ExtractNicknameFromColormarker(byte[] data)
        {
            // Паттерн: 0c <outer_len> 01 0c <nick_len> <nick_bytes> ...
            // или просто ищем первую валидную строку после начальных служебных байт
            for (int i = 0; i < data.Length - 3; i++)
            {
                // Паттерн: tag 0c или 09, затем длина, затем ASCII-строка (ник)
                if ((data[i] == 0x09 || (data[i] == 0x0c && i + 1 < data.Length)) && i + 1 < data.Length)
                {
                    int nlen = data[i + 1];
                    if (nlen < 2 || nlen > 36 || i + 2 + nlen > data.Length) continue;
                    string candidate;
                    try { candidate = Encoding.UTF8.GetString(data, i + 2, nlen); }
                    catch { continue; }
                    if (IsValidNickname(candidate)) return candidate;
                }
            }
            return null;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Парсинг конфиг-файлов маркеров
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// H2N3 .cg binary — repeated records:
        ///   0a &lt;rec_len&gt; [ 0a &lt;inner_len&gt; ... ] 12 &lt;len&gt; &lt;color&gt; 1a &lt;len&gt; &lt;desc&gt;
        /// Ключевой момент: нужно пропустить inner sub-message (UUID), иначе его байты
        /// будут распознаны как ложные теги.
        /// </summary>
        private static List<FishMarkerEntry> ParseH2N3Cg(byte[] data)
        {
            var result = new List<FishMarkerEntry>();
            int i = 0;
            while (i < data.Length - 1)
            {
                if (data[i] != 0x0a) { i++; continue; }
                int recLen = data[i + 1];
                if (recLen == 0 || i + 2 + recLen > data.Length) { i++; continue; }

                byte[] rec = new byte[recLen];
                Buffer.BlockCopy(data, i + 2, rec, 0, recLen);

                // Пропускаем inner sub-message (UUID, тег 0x0a)
                int j = 0;
                if (j < rec.Length && rec[j] == 0x0a && j + 1 < rec.Length)
                    j = j + 2 + rec[j + 1];

                string rawColor = null, desc = null;
                while (j < rec.Length - 1)
                {
                    byte tag = rec[j];
                    int flen = rec[j + 1];
                    if (j + 2 + flen > rec.Length) break;

                    if (tag == 0x12)
                        try { rawColor = Encoding.ASCII.GetString(rec, j + 2, flen); } catch { }
                    else if (tag == 0x1a)
                        try { desc = Encoding.UTF8.GetString(rec, j + 2, flen); } catch { }

                    j += 2 + flen;
                }

                if (!string.IsNullOrWhiteSpace(desc))
                {
                    string hex = NormalizeColor(rawColor);
                    if (hex != null)
                        result.Add(new FishMarkerEntry(desc.Trim(), true, hex));
                }

                i = i + 2 + recLen;
            }
            return Deduplicate(result);
        }

        /// <summary>
        /// H2N4 .h2nconfig binary:
        ///   0x14 &lt;len&gt; &lt;desc&gt; 0x1c &lt;len&gt; &lt;color&gt; 0x20 0x01
        /// </summary>
        private static List<FishMarkerEntry> ParseH2N4Config(byte[] data)
        {
            var result = new List<FishMarkerEntry>();
            int i = 0;
            while (i < data.Length - 4)
            {
                if (data[i] != 0x14) { i++; continue; }
                int dlen = data[i + 1];
                if (dlen == 0 || dlen >= 64 || i + 2 + dlen >= data.Length) { i++; continue; }
                if (data[i + 2 + dlen] != 0x1c) { i++; continue; }

                string desc;
                try { desc = Encoding.UTF8.GetString(data, i + 2, dlen); }
                catch { i++; continue; }
                if (!IsValidDescription(desc)) { i++; continue; }

                int afterDesc = i + 2 + dlen;
                int colorLen = data[afterDesc + 1];
                if (afterDesc + 2 + colorLen > data.Length) { i++; continue; }

                string rawColor;
                try { rawColor = Encoding.ASCII.GetString(data, afterDesc + 2, colorLen); }
                catch { i++; continue; }

                string hex = NormalizeColor(rawColor);
                if (hex != null)
                    result.Add(new FishMarkerEntry(desc.Trim(), true, hex));

                i = afterDesc + 2 + colorLen;
            }
            return Deduplicate(result);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Утилиты
        // ═════════════════════════════════════════════════════════════════════

        private static readonly Dictionary<string, string> NamedColors
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Red",    "#FF0000" }, { "Green",  "#008000" }, { "Blue",   "#0000FF" },
            { "White",  "#FFFFFF" }, { "Black",  "#000000" }, { "Yellow", "#FFFF00" },
            { "Orange", "#FFA500" }, { "Purple", "#800080" }, { "Pink",   "#FFC0CB" },
            { "Cyan",   "#00FFFF" }, { "Magenta","#FF00FF" }, { "Gray",   "#808080" },
            { "Grey",   "#808080" }, { "Brown",  "#A52A2A" }, { "Lime",   "#00FF00" },
        };

        /// <summary>
        /// Нормализует цвет из формата трекера в #RRGGBB.
        ///   Именованный «Red»    → #FF0000
        ///   #RRGGBB (6 символов) → без изменений
        ///   #AARRGGBB (8, H2N3)  → если первые 2 = «FF» → берём [2..7]
        ///   #RRGGBBAA (8, H2N4)  → берём [0..5]
        /// </summary>
        public static string NormalizeColor(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            if (!s.StartsWith("#"))
                return NamedColors.TryGetValue(s, out var nc) ? nc : null;
            string h = s.Substring(1).ToUpperInvariant();
            if (h.Length == 6) return "#" + h;
            if (h.Length == 8)
                return "#" + (h.Substring(0, 2) == "FF" ? h.Substring(2, 6) : h.Substring(0, 6));
            return null;
        }

        private static bool IsValidNickname(string s)
            => !string.IsNullOrEmpty(s) && s.Length >= 2 && s.Length <= 36
               && s.All(c => c >= 0x20 && c < 0x7F || c > 0x80);

        private static bool IsValidDescription(string s)
            => !string.IsNullOrEmpty(s) && s.Length >= 1 && s.Length < 64
               && s.All(c => c >= 0x20);

        private static string SanitizeForFilename(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private static List<FishMarkerEntry> Deduplicate(List<FishMarkerEntry> list)
            => list.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                   .Select(g => g.First()).ToList();
    }
}