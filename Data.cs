using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CounterPickMaster;

internal sealed record Champion(int Id, string English, string Russian);
internal sealed record Counter(int Id, double Rate, int Games);
internal sealed record Snapshot(string Patch, DateTimeOffset SourceTime, DateTimeOffset CheckedAt,
    Champion[] Champions, Dictionary<string, Counter[]> Matchups, DateOnly? PatchNotesDate = null, string? Tier = null)
{
    public Counter[] Find(string role, int champion) =>
        Matchups.GetValueOrDefault(Key(role, champion)) ?? [];

    public static string Key(string role, int champion) => $"{role}/{champion}";

    public Counter[] Ranked(string role, int champion) => Find(role, champion)
        .Where(counter => counter.Rate < 0.5)
        .OrderBy(counter => counter.Rate)
        .ThenByDescending(counter => counter.Games)
        .ThenBy(counter => counter.Id)
        .ToArray();
}

internal sealed class DataService(string cachePath)
{
    public static readonly string[] Roles = ["top", "mid", "jungle", "adc", "support"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(18) };
    private const string Rankings = "https://op.gg/ru/lol/champions";
    private const string GoldFilters = "tier=gold&region=global&type=ranked";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    private const string Dragon = "https://ddragon.leagueoflegends.com";

    public static string Normalize(string input) =>
        new(input.Where(char.IsLetterOrDigit).Select(c => char.ToLowerInvariant(c) == 'ё'
            ? 'е' : char.ToLowerInvariant(c)).ToArray());

    public static string[] SearchNames(Champion champion) =>
        [Normalize(champion.Russian), Normalize(champion.English)];

    public static IEnumerable<Champion> Search(Champion[] champions, string query)
    {
        var key = Normalize(query);
        return champions
            .Select(champion => (Champion: champion, Names: SearchNames(champion)))
            .Where(row => row.Names.Any(name => name.Contains(key, StringComparison.Ordinal)))
            .OrderByDescending(row => row.Names.Any(name => name == key))
            .ThenByDescending(row => row.Names.Any(name => name.StartsWith(key, StringComparison.Ordinal)))
            .ThenBy(row => row.Champion.Russian, StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true))
            .Select(row => row.Champion);
    }

    private static bool Valid(Snapshot? snapshot) => snapshot is
    {
        Tier: "gold", Patch.Length: > 0, Champions.Length: > 100, Matchups.Count: > 100
    };

    public async Task<Snapshot?> LoadCacheAsync(CancellationToken token)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                using var file = File.OpenRead(cachePath);
                var saved = await JsonSerializer.DeserializeAsync<Snapshot>(file, cancellationToken: token);
                if (Valid(saved)) return saved;
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupted or unreadable cache can be replaced by the next successful synchronization.
        }
        using var packaged = typeof(DataService).Assembly.GetManifestResourceStream("CounterPickMaster.initial-snapshot.json");
        var embedded = packaged is null ? null :
            await JsonSerializer.DeserializeAsync<Snapshot>(packaged, cancellationToken: token);
        return Valid(embedded) ? embedded : null;
    }

    private async Task SaveAsync(Snapshot snapshot, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = File.Create(temporary))
                await JsonSerializer.SerializeAsync(output, snapshot, cancellationToken: token);
            File.Move(temporary, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<Snapshot> RefreshAsync(Snapshot? previous, IProgress<string> status, CancellationToken token)
    {
        status.Report("Проверяем патч OP.GG для Золота…");
        string firstPage = await GoldPageAsync($"{Rankings}/illaoi/counters/top?{GoldFilters}", token);
        string patch = PagePatch(firstPage);
        if (Valid(previous) && previous!.Patch == patch &&
            DateTimeOffset.UtcNow - previous.SourceTime < RefreshInterval)
        {
            return previous with { CheckedAt = DateTimeOffset.Now };
        }

        status.Report("Получаем список чемпионов Золота по ролям…");
        var catalogTask = ChampionsAsync(token);
        var rolesTask = Task.WhenAll(Roles.Select(async role =>
        {
            string html = await GoldPageAsync($"{Rankings}?position={role}&{GoldFilters}&patch={patch}", token, rankings: true);
            string[] slugs = Regex.Matches(WebUtility.HtmlDecode(html),
                    $@"/lol/champions/([a-z0-9]+)/build/{role}\?{GoldFilters}&patch={Regex.Escape(patch)}")
                .Select(match => match.Groups[1].Value).Distinct().ToArray();
            if (slugs.Length == 0) throw new InvalidDataException($"Список роли {role} пуст.");
            return slugs.Select(slug => (Role: role, Slug: slug));
        }));
        await Task.WhenAll(catalogTask, rolesTask);
        var bySlug = await catalogTask;
        var jobs = (await rolesTask).SelectMany(role => role).ToArray();
        var matchups = new System.Collections.Concurrent.ConcurrentDictionary<string, Counter[]>();
        int completed = 0;
        await Parallel.ForEachAsync(jobs, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4, CancellationToken = token
        }, async (job, cancellation) =>
        {
            if (!bySlug.TryGetValue(job.Slug, out var champion))
                throw new InvalidDataException($"В справочнике Riot отсутствует чемпион OP.GG: {job.Slug}.");
            try
            {
                string html = job == ("top", "illaoi") ? firstPage :
                    await GoldPageAsync($"{Rankings}/{job.Slug}/counters/{job.Role}?{GoldFilters}&patch={patch}", cancellation);
                if (PagePatch(html) != patch) throw new InvalidDataException("Патч OP.GG сменился во время загрузки.");
                matchups[Snapshot.Key(job.Role, champion.Id)] = ReadMatchups(html, bySlug, champion.Id);
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException)
            {
                throw new InvalidDataException($"OP.GG, {job.Slug}/{job.Role}: {error.Message}", error);
            }
            int count = Interlocked.Increment(ref completed);
            status.Report($"Обновляем данные OP.GG… {count}/{jobs.Length}");
        });

        var now = DateTimeOffset.Now;
        var patchDate = await PatchNotesDateAsync(patch, now, token);
        var snapshot = new Snapshot(patch, now, now, bySlug.Values.DistinctBy(champion => champion.Id).ToArray(),
            matchups.ToDictionary(), patchDate, "gold");
        if (!Valid(snapshot)) throw new InvalidDataException("OP.GG вернул неполный список ролей.");
        await SaveAsync(snapshot, token);
        return snapshot;
    }

    private static async Task<Dictionary<string, Champion>> ChampionsAsync(CancellationToken token)
    {
        var versions = await Http.GetFromJsonAsync<string[]>(
            $"{Dragon}/api/versions.json", token);
        var version = versions?.FirstOrDefault() ?? throw new InvalidDataException("Riot не указал версию справочника.");
        var en = CatalogAsync(version, "en_US", token);
        var ru = CatalogAsync(version, "ru_RU", token);
        await Task.WhenAll(en, ru);
        var translations = await ru;
        return (await en).ToDictionary(pair => pair.Key, pair => new Champion(pair.Value.Id, pair.Value.Name,
            translations.GetValueOrDefault(pair.Key, pair.Value).Name));
    }


    private static async Task<DateOnly?> PatchNotesDateAsync(
        string patch, DateTimeOffset sourceTime, CancellationToken token)
    {
        var match = Regex.Match(patch, @"^(\d{1,2})\.(\d{1,2})$");
        if (!match.Success) return null;

        // Riot names the current season's notes 26.x while Data Dragon and OP.GG use 16.x.
        int season = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int calendarSeason = sourceTime.Year % 100;
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using (client)
        {
            string suffix = match.Groups[2].Value;
            foreach (int major in new[] { calendarSeason, season }.Distinct())
            {
                try
                {
                    string url = $"https://www.leagueoflegends.com/en-us/news/game-updates/league-of-legends-patch-{major}-{suffix}-notes/";
                    using var response = await client.GetAsync(url, token);
                    if (!response.IsSuccessStatusCode) continue;
                    string html = await response.Content.ReadAsStringAsync(token);
                    var published = Regex.Match(html, @"""datePublished""\s*:\s*""([^""]+)""");
                    if (!published.Success || !DateTimeOffset.TryParse(published.Groups[1].Value,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                    var distance = sourceTime.UtcDateTime.Date - date.UtcDateTime.Date;
                    if (distance.Days is >= 0 and <= 24) return DateOnly.FromDateTime(date.UtcDateTime);
                }
                catch (Exception error) when (!token.IsCancellationRequested &&
                    error is HttpRequestException or TaskCanceledException) { }
            }
        }
        return null;
    }

    private static async Task<Dictionary<string, (int Id, string Name)>> CatalogAsync(
        string version, string locale, CancellationToken token)
    {
        using var doc = await Http.GetFromJsonAsync<JsonDocument>(
            $"{Dragon}/cdn/{version}/data/{locale}/champion.json", token)
            ?? throw new InvalidDataException("Riot вернул пустой справочник.");
        return doc.RootElement.GetProperty("data").EnumerateObject().ToDictionary(
            entry => entry.Name.ToLowerInvariant(),
            entry => (int.Parse(entry.Value.GetProperty("key").GetString()!, CultureInfo.InvariantCulture),
                entry.Value.GetProperty("name").GetString()!));
    }

    private static async Task<string> GoldPageAsync(string url, CancellationToken token, bool rankings = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("CounterPickMaster/1.0");
                using var response = await Http.SendAsync(request, token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
                {
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(3 * (attempt + 1)), token);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                string html = await response.Content.ReadAsStringAsync(token);
                string data = Regex.Replace(html, @"\\+""", @"""");
                if (!rankings && (!data.Contains(@"""selectedValue"":""gold""", StringComparison.Ordinal) ||
                    !data.Contains(@"""selectedValue"":""global""", StringComparison.Ordinal)))
                    throw new InvalidDataException("OP.GG не подтвердил фильтры Золото и все серверы.");
                return data;
            }
            catch (Exception error) when (attempt < 2 && !token.IsCancellationRequested &&
                (error is HttpRequestException { StatusCode: null or >= HttpStatusCode.InternalServerError } or TaskCanceledException))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token);
            }
        }
    }

    private static string PagePatch(string html)
    {
        var version = Regex.Match(html, @"""selectedValue"":""(\d+\.\d+)"",""versions"":\[");
        if (!version.Success) version = Regex.Match(html, @"<title>[^<]*\b(?:патч|patch)\s+(\d+\.\d+)", RegexOptions.IgnoreCase);
        return version.Success ? version.Groups[1].Value :
            throw new InvalidDataException("OP.GG не указал патч таблицы.");
    }

    private static Counter[] ReadMatchups(string html, Dictionary<string, Champion> bySlug, int selectedId)
    {
        var table = Regex.Match(html,
            @"""data"":(\[(?:\{""play"":[^\[\]]*\})?\]),""targetChampion"":");
        if (!table.Success) throw new InvalidDataException("Не найдена таблица матчапов OP.GG.");

        using var document = JsonDocument.Parse(table.Groups[1].Value);
        var counters = new Dictionary<int, Counter>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            string slug = row.GetProperty("champion").GetProperty("key").GetString()!;
            if (!bySlug.TryGetValue(slug, out var opponent))
                throw new InvalidDataException($"В справочнике Riot отсутствует соперник OP.GG: {slug}.");
            int games = row.GetProperty("play").GetInt32();
            double rate = row.GetProperty("win_rate").GetDouble() / 100;
            if (opponent.Id != selectedId && games > 0 && rate is >= 0 and <= 1)
                counters.TryAdd(opponent.Id, new Counter(opponent.Id, rate, games));
        }
        return counters.Values.ToArray();
    }
}
