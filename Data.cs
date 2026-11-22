using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CounterPickMaster;

internal sealed record Champion(int Id, string English, string Russian);
internal sealed record Counter(int Id, double Rate, int Games);
internal sealed record Snapshot(string Patch, DateTimeOffset SourceTime, DateTimeOffset CheckedAt,
    Champion[] Champions, Dictionary<string, Counter[]> Matchups, DateOnly? PatchNotesDate = null)
{
    public Counter[] Find(string role, int champion) =>
        Matchups.GetValueOrDefault(Key(role, champion)) ?? [];

    public static string Key(string role, int champion) => $"{role}/{champion}";

    public Counter[] Ranked(string role, int champion) => Find(role, champion)
        .OrderByDescending(counter => counter.Rate)
        .ThenByDescending(counter => counter.Games)
        .ThenBy(counter => counter.Id)
        .Take(8).ToArray();
}

internal sealed class DataService(string cachePath)
{
    public static readonly string[] Roles = ["top", "mid", "jungle", "adc", "support"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(18) };
    private static readonly string[] AnalysisFields =
    [
        "data.weak_counters[].{champion_id,counter_win_rate,play}",
        "data.strong_counters[].{champion_id,my_win_rate,play}",
        "data.summary.positions[].name",
        "data.summary.positions[].counters[].{champion_id,play,win}",
        "data.trends.win.{version,created_at}"
    ];
    private const string Mcp = "https://mcp-api.op.gg/mcp";
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
        Patch.Length: > 0, Champions.Length: > 100, Matchups.Count: > 100
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
        return packaged is null ? null : await JsonSerializer.DeserializeAsync<Snapshot>(packaged, cancellationToken: token);
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
        status.Report("Проверяем патч OP.GG и список чемпионов…");
        var probeTask = AnalyzeAsync("Trundle", "top", token);
        var championsTask = ChampionsAsync(token);
        await Task.WhenAll(probeTask, championsTask);
        var probe = await probeTask;
        var champions = await championsTask;
        if (probe.Patch is null || probe.SourceTime is null)
            throw new InvalidDataException("OP.GG не указал версию статистики.");

        if (Valid(previous) && previous!.Patch == probe.Patch && previous.SourceTime == probe.SourceTime)
        {
            var current = previous with
            {
                Champions = champions, CheckedAt = DateTimeOffset.Now,
                PatchNotesDate = previous.PatchNotesDate ??
                    await PatchNotesDateAsync(probe.Patch, probe.SourceTime.Value, token)
            };
            await SaveAsync(current, token);
            return current;
        }

        status.Report("Получаем список чемпионов по ролям…");
        var names = await RoleNamesAsync(token);
        var byName = champions.ToDictionary(champion => Normalize(champion.English));
        var knownIds = champions.Select(champion => champion.Id).ToHashSet();
        var jobs = names.SelectMany((roleNames, index) => roleNames.Distinct()
            .Select(name =>
            {
                if (!byName.TryGetValue(Normalize(name), out var champion))
                    throw new InvalidDataException($"В справочнике Riot пока нет чемпиона {name}.");
                return (Role: Roles[index], Champion: champion);
            })).ToArray();

        var analyses = new System.Collections.Concurrent.ConcurrentDictionary<(string Role, int Champion), Analysis>();
        int completed = 0;
        await Parallel.ForEachAsync(jobs, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4, CancellationToken = token
        }, async (job, cancellation) =>
        {
            var analysis = job == ("top", byName["trundle"]) ? probe :
                await AnalyzeAsync(job.Champion.English, job.Role, cancellation);
            if (analysis.Patch is not null && analysis.Patch != probe.Patch)
                throw new InvalidDataException("Патч OP.GG сменился во время загрузки. Повторите обновление.");
            analyses[(job.Role, job.Champion.Id)] = analysis;
            int count = Interlocked.Increment(ref completed);
            status.Report($"Обновляем данные OP.GG… {count}/{jobs.Length}");
        });

        var latest = await AnalyzeAsync("Trundle", "top", token);
        if (latest.Patch != probe.Patch || latest.SourceTime != probe.SourceTime)
            throw new InvalidDataException("OP.GG обновил статистику во время загрузки. Повторите обновление.");
        var patchNotesDate = await PatchNotesDateAsync(probe.Patch, probe.SourceTime.Value, token);
        var matchups = analyses.ToDictionary(pair => pair.Key, pair => pair.Value.Counters
            .Where(counter => knownIds.Contains(counter.Id) && counter.Id != pair.Key.Champion)
            .ToDictionary(counter => counter.Id));

        // A favorable matchup reported for the opposing champion is also a counter pick.
        foreach (var (key, analysis) in analyses)
            foreach (var opponent in analysis.Favorable)
                if (opponent.Id != key.Champion && matchups.TryGetValue((key.Role, opponent.Id), out var target))
                    target.TryAdd(key.Champion, new Counter(key.Champion, opponent.Rate, opponent.Games));

        var snapshot = new Snapshot(probe.Patch, probe.SourceTime.Value, DateTimeOffset.Now,
            champions, matchups.ToDictionary(pair => Snapshot.Key(pair.Key.Role, pair.Key.Champion),
                pair => pair.Value.Values.ToArray()), patchNotesDate);
        if (!Valid(snapshot)) throw new InvalidDataException("OP.GG вернул неполный список ролей.");
        await SaveAsync(snapshot, token);
        return snapshot;
    }

    private static async Task<Champion[]> ChampionsAsync(CancellationToken token)
    {
        var versions = await Http.GetFromJsonAsync<string[]>(Dragon + "/api/versions.json", token);
        var version = versions?.FirstOrDefault() ?? throw new InvalidDataException("Riot не указал версию справочника.");
        var en = CatalogAsync(version, "en_US", token);
        var ru = CatalogAsync(version, "ru_RU", token);
        await Task.WhenAll(en, ru);
        var translations = await ru;
        return (await en).Select(pair => new Champion(pair.Key, pair.Value,
            translations.GetValueOrDefault(pair.Key, pair.Value))).ToArray();
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

    private static async Task<Dictionary<int, string>> CatalogAsync(string version, string locale, CancellationToken token)
    {
        using var doc = await Http.GetFromJsonAsync<JsonDocument>(
            $"{Dragon}/cdn/{version}/data/{locale}/champion.json", token)
            ?? throw new InvalidDataException("Riot вернул пустой справочник.");
        return doc.RootElement.GetProperty("data").EnumerateObject().ToDictionary(
            entry => int.Parse(entry.Value.GetProperty("key").GetString()!, CultureInfo.InvariantCulture),
            entry => entry.Value.GetProperty("name").GetString()!);
    }

    private static async Task<string[][]> RoleNamesAsync(CancellationToken token)
    {
        var response = await CallAsync("lol_list_lane_meta_champions", new
        {
            position = "all",
            desired_output_fields = Roles.Select(role => $"data.positions.{role}[].{{champion}}")
        }, token);
        var positions = response.Classes["Positions"];
        var groups = Regex.Matches(response.Body, @"\[([^\[\]]*)\]");
        if (groups.Count != positions.Length) throw new InvalidDataException("Изменился формат списка ролей OP.GG.");
        return Roles.Select(role =>
        {
            int index = Array.IndexOf(positions, role);
            if (index < 0) throw new InvalidDataException($"OP.GG не вернул роль {role}.");
            var names = Regex.Matches(groups[index].Groups[1].Value, JsonString)
                .Select(match => JsonSerializer.Deserialize<string>(match.Value)!).ToArray();
            return names.Length > 0 ? names : throw new InvalidDataException($"Список роли {role} пуст.");
        }).ToArray();
    }

    private sealed record Analysis(string? Patch, DateTimeOffset? SourceTime, Counter[] Counters, Counter[] Favorable);

    private static string ChampionCode(string name) =>
        Regex.Replace(name.Replace(" & ", " ").Replace("'", "").Replace(".", ""), @"\s+", "_").ToUpperInvariant();

    private static async Task<Analysis> AnalyzeAsync(string name, string role, CancellationToken token)
    {
        var response = await CallAsync("lol_get_champion_analysis", new
        {
            champion = ChampionCode(name), position = role, game_mode = "ranked", tier = "ibsg",
            desired_output_fields = AnalysisFields
        }, token);

        var source = response.Rows("Win").FirstOrDefault();
        var patch = source?.GetValueOrDefault("version").GetString();
        DateTimeOffset? date = DateTimeOffset.TryParse(source?.GetValueOrDefault("created_at").GetString(),
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

        var counters = new Dictionary<int, Counter>();
        foreach (var row in response.Rows("WeakCounter"))
        {
            var counter = new Counter(row["champion_id"].GetInt32(), row["counter_win_rate"].GetDouble(),
                row["play"].GetInt32());
            if (counter.Games > 0 && counter.Rate is >= 0 and <= 1) counters[counter.Id] = counter;
        }

        var position = Regex.Match(response.Body, @"Position\(""" + role.ToUpperInvariant() + @"""\s*,\s*\[([^\]]*)\]");
        if (position.Success)
        {
            foreach (var row in response.Rows("Counter", position.Groups[1].Value))
            {
                int id = row["champion_id"].GetInt32();
                int games = row["play"].GetInt32();
                int selectedWins = row["win"].GetInt32();
                if (games > 0 && selectedWins >= 0 && selectedWins < games - selectedWins)
                    counters.TryAdd(id, new Counter(id, (double)(games - selectedWins) / games, games));
            }
        }
        var favorable = response.Rows("StrongCounter").Select(row => new Counter(
            row["champion_id"].GetInt32(), row["my_win_rate"].GetDouble(), row["play"].GetInt32()))
            .Where(counter => counter.Games > 0 && counter.Rate is > 0.5 and <= 1).ToArray();
        return new Analysis(patch, date, counters.Values.ToArray(), favorable);
    }

    private const string JsonString = @"""(?:[^""\\]|\\.)*""";
    private sealed class Records(string text)
    {
        public string Body { get; } = text.Replace("\r", "").Split("\n\n", 2).Last();
        public Dictionary<string, string[]> Classes { get; } = Regex.Matches(text, @"(?m)^class (\w+): ([^\r\n]+)")
            .ToDictionary(match => match.Groups[1].Value,
                match => match.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries));

        public IEnumerable<Dictionary<string, JsonElement>> Rows(string type, string? body = null)
        {
            if (!Classes.TryGetValue(type, out var names)) yield break;
            foreach (Match match in Regex.Matches(body ?? Body,
                $@"\b{type}\(((?:(?:{JsonString})|[^""()])*)\)"))
            {
                using var row = JsonDocument.Parse("[" + match.Groups[1].Value + "]");
                var values = row.RootElement.EnumerateArray().Select(value => value.Clone()).ToArray();
                if (values.Length != names.Length) throw new InvalidDataException($"Изменился формат OP.GG ({type}).");
                yield return names.Zip(values).ToDictionary(pair => pair.First, pair => pair.Second);
            }
        }
    }

    private static async Task<Records> CallAsync(string name, object arguments, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Mcp)
                {
                    Content = JsonContent.Create(new
                    {
                        jsonrpc = "2.0", id = 1, method = "tools/call",
                        @params = new { name, arguments }
                    })
                };
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("text/event-stream");
                using var response = await Http.SendAsync(request, token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(3 * (attempt + 1));
                    await Task.Delay(delay, token);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                string payload = await response.Content.ReadAsStringAsync(token);
                if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                    payload = payload.Split('\n').Last(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
                using var json = JsonDocument.Parse(payload);
                if (json.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidDataException($"OP.GG ({name}): {error.GetProperty("message").GetString()}");
                var result = json.RootElement.GetProperty("result");
                var returned = result.GetProperty("content")[0].GetProperty("text").GetString()!;
                if (result.TryGetProperty("isError", out var failure) && failure.GetBoolean())
                    throw new InvalidDataException($"OP.GG ({name}): {returned}");
                if (!returned.StartsWith("class ", StringComparison.Ordinal))
                    throw new InvalidDataException($"OP.GG ({name}) изменил формат данных.");
                return new Records(returned);
            }
            catch (Exception error) when (attempt < 2 && !token.IsCancellationRequested &&
                (error is HttpRequestException { StatusCode: null or >= HttpStatusCode.InternalServerError } or TaskCanceledException))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token);
            }
        }
    }
}
