using Microsoft.Playwright;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

class Program
{
    public class EtaRankingDb
    {
        public string CollectDate { get; set; } = string.Empty;
        // 사이트 상단 "Last Update :" 값 (예: 2026-09-15 08:16:44). 같은 값이면 다시 수집하지 않는다.
        public string? LastUpdate { get; set; }
        public Dictionary<string, List<RankingItem>> Servers { get; set; } = new();
    }

    // 서버 → 캐릭터 → 날짜 → 인원수 누적 기록
    public class EtaHistoryDb
    {
        // 수집일 → 그날 사이트의 Last Update 값 (eta_ranking.json은 덮어써지므로 여기 누적)
        public Dictionary<string, string> LastUpdate { get; set; } = new();
        public Dictionary<string, Dictionary<string, Dictionary<string, int>>> Servers { get; set; } = new();
    }

    // 사이트 랭킹 페이지의 cc 파라미터 순서 (가나다순 아님)
    private static readonly string[] CharacterNames =
    {
        "루시안", "보리스", "막시민", "시벨린", "조슈아", "란지에", "이자크",
        "밀라", "티치엘", "이스핀", "나야트레이", "아나이스", "클로에",
        "벤야", "이솔렛", "로아미니", "녹턴", "리체", "예프넨"
    };

    public class RankingItem
    {
        public int CharacterCode { get; set; }
        public int Rank { get; set; }
        public string? UserId { get; set; }
        public int Level { get; set; }
        public long Essence { get; set; }
    }

    // Last Update를 읽어 오는 페이지. 값은 서버(sc)와 무관하게 동일하다.
    private const string LastUpdateProbeUrl = "https://tales.nexon.com/Community/Ranking/EtaRank?sc=16";
    private const string LastUpdateSelector = "dl.dl_update dd";

    // GitHub 러너는 UTC라 DateTime.Now를 쓰면 날짜가 하루 어긋난다. 모든 날짜는 KST 기준.
    private static readonly TimeZoneInfo Kst = FindKst();
    private static DateTime NowKst => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Kst);

    public static async Task Main()
    {
        // 1. 환경 감지: GitHub Actions인지 확인
        bool isGithubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

        // 실행 옵션 (환경 변수)
        //  ETA_WAIT_FOR_UPDATE=1  : 사이트 Last Update가 바뀔 때까지 기다렸다가 수집 (예약 실행용)
        //  ETA_POLL_INTERVAL_MIN  : 재확인 간격(분), 기본 5
        //  ETA_WAIT_MAX_MIN       : 최대 대기(분), 기본 240. 넘기면 수집하지 않고 종료
        //  ETA_SETTLE_SEC         : 갱신 감지 후 수집 전 대기(초), 기본 60
        //  ETA_FORCE=1            : Last Update가 같아도 수집
        //  ETA_CHECK_ONLY=1       : Last Update만 읽고 종료 (점검용)
        bool waitForUpdate = EnvFlag("ETA_WAIT_FOR_UPDATE");
        bool force = EnvFlag("ETA_FORCE");
        bool checkOnly = EnvFlag("ETA_CHECK_ONLY");
        int pollIntervalMin = EnvInt("ETA_POLL_INTERVAL_MIN", 5);
        int waitMaxMin = EnvInt("ETA_WAIT_MAX_MIN", 240);
        int settleSec = EnvInt("ETA_SETTLE_SEC", 60);

        // 2. 저장 경로 설정
        //    GitHub Actions에서는 실행 위치(Working Directory)가 저장소 루트, 로컬에서는 현재 폴더.
        string fileName = "eta_ranking.json";
        string filePath = isGithubActions ? Path.Combine(Directory.GetCurrentDirectory(), fileName) : fileName;
        string historyFileName = "eta_history.json";
        string historyPath = isGithubActions ? Path.Combine(Directory.GetCurrentDirectory(), historyFileName) : historyFileName;

        string? storedLastUpdate = ReadStoredLastUpdate(filePath);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true // 서버 실행을 위해 Headless 고정
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
        });
        var page = await context.NewPageAsync();

        // 3. 사이트 Last Update 확인 (필요하면 바뀔 때까지 대기)
        string? lastUpdate = await ReadSiteLastUpdateAsync(page);
        Console.WriteLine($"사이트 Last Update: {lastUpdate ?? "(읽기 실패)"} / 마지막 수집분: {storedLastUpdate ?? "(기록 없음)"} / 현재 KST {NowKst:yyyy-MM-dd HH:mm}");

        if (waitForUpdate)
        {
            var deadline = DateTime.UtcNow.AddMinutes(waitMaxMin);
            while (!IsNewUpdate(lastUpdate, storedLastUpdate))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    Console.WriteLine($"::warning::{waitMaxMin}분 동안 Last Update가 바뀌지 않아 수집하지 않고 종료합니다. (사이트 {lastUpdate ?? "읽기 실패"}, 수집분 {storedLastUpdate ?? "없음"})");
                    return;
                }
                Console.WriteLine($"아직 갱신 전입니다. {pollIntervalMin}분 후 다시 확인합니다. ({NowKst:HH:mm} KST)");
                await Task.Delay(TimeSpan.FromMinutes(pollIntervalMin));
                lastUpdate = await ReadSiteLastUpdateAsync(page);
            }
            Console.WriteLine($"갱신 감지: {lastUpdate} ({NowKst:HH:mm} KST). {settleSec}초 뒤 수집을 시작합니다.");
            if (settleSec > 0) await Task.Delay(TimeSpan.FromSeconds(settleSec));
        }
        else if (!force && lastUpdate != null && lastUpdate == storedLastUpdate)
        {
            Console.WriteLine($"사이트 Last Update({lastUpdate})가 마지막 수집분과 같아 수집을 건너뜁니다. 다시 수집하려면 ETA_FORCE=1.");
            return;
        }

        if (checkOnly)
        {
            Console.WriteLine("ETA_CHECK_ONLY=1: 여기서 종료합니다.");
            return;
        }

        var db = new EtaRankingDb
        {
            CollectDate = NowKst.ToString("yyyy-MM-dd"),
            LastUpdate = lastUpdate
        };
        var servers = new (int Sc, string Name)[]
        {
            (7, "하이아칸"),
            (16, "네냐플")
        };
        var emptyCodes = new List<string>();

        // 4. 수집 로직
        foreach (var (sc, serverName) in servers)
        {
            var rankings = new List<RankingItem>();
            db.Servers[serverName] = rankings;

            for (int cc = 0; cc <= 18; cc++)
            {
                Console.WriteLine($"[{serverName}] 캐릭터 코드 {cc} 수집 시작...");
                int collectedForCode = 0;

                for (int p = 1; p <= 50; p++)
                {
                    string url = $"https://tales.nexon.com/Community/Ranking/EtaRank?sc={sc}&cc={cc}&pagesize=100&page={p}";

                    // 일시적인 로딩 실패로 캐릭터 전체를 버리지 않도록 재시도한다.
                    bool loaded = false;
                    for (int attempt = 1; attempt <= 3 && !loaded; attempt++)
                    {
                        try
                        {
                            // NetworkIdle은 이 사이트에서 도달하지 않는다(트래킹 비콘이 계속 돈다).
                            // DOMContentLoaded로 받고, 실제 준비 여부는 셀렉터로 판단한다.
                            await page.GotoAsync(url, new PageGotoOptions
                            {
                                WaitUntil = WaitUntilState.DOMContentLoaded,
                                Timeout = 60000
                            });
                            await page.WaitForSelectorAsync("table tbody tr", new PageWaitForSelectorOptions { Timeout = 20000 });
                            loaded = true;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($" - {p}페이지 로딩 실패 ({attempt}/3): {ex.GetType().Name}");
                            if (attempt < 3) await Task.Delay(3000 * attempt);
                        }
                    }
                    if (!loaded) break;

                    var rows = await page.QuerySelectorAllAsync("table tbody tr");
                    int dataRows = 0;

                    foreach (var row in rows)
                    {
                        var cols = await row.QuerySelectorAllAsync("td");
                        // 페이지 상단 검색 필터도 table이라 td 4개 이상인 행만 데이터로 본다.
                        if (cols.Count >= 4)
                        {
                            string rankText = (await cols[0].InnerTextAsync()).Trim();
                            string nameText = (await cols[1].InnerTextAsync()).Trim();
                            string levelText = (await cols[2].InnerTextAsync()).Trim();
                            string essenceText = (await cols[3].InnerTextAsync()).Trim().Replace(",", "");

                            rankings.Add(new RankingItem
                            {
                                CharacterCode = cc,
                                Rank = int.TryParse(rankText, out int r) ? r : 0,
                                UserId = ExtractId(nameText),
                                Level = int.TryParse(levelText, out int l) ? l : 0,
                                Essence = long.TryParse(essenceText, out long e) ? e : 0
                            });
                            dataRows++;
                        }
                    }

                    collectedForCode += dataRows;
                    Console.WriteLine($" - {p}페이지 완료 (데이터 {dataRows}행, 누적 {rankings.Count}명)");
                    if (dataRows < 100) break;
                    await Task.Delay(500);
                }

                if (collectedForCode == 0)
                {
                    Console.WriteLine($"::warning::[{serverName}] 캐릭터 코드 {cc} 수집 결과가 0건입니다.");
                    emptyCodes.Add($"{serverName}/cc={cc}");
                }
            }
        }

        if (emptyCodes.Count > 0)
            Console.WriteLine($"::warning::데이터가 비어있는 캐릭터 코드: {string.Join(", ", emptyCodes)}");

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        string jsonString = JsonSerializer.Serialize(db, jsonOptions);

        // 지정된 경로에 파일 쓰기
        await File.WriteAllTextAsync(filePath, jsonString, Encoding.UTF8);
        Console.WriteLine($"최종 파일 저장 위치: {Path.GetFullPath(filePath)}");

        // 5. 캐릭터별 인원수 히스토리 갱신 (기존 파일에 오늘 날짜를 누적)
        var history = new EtaHistoryDb();
        if (File.Exists(historyPath))
        {
            try
            {
                history = JsonSerializer.Deserialize<EtaHistoryDb>(await File.ReadAllTextAsync(historyPath))
                          ?? new EtaHistoryDb();
            }
            catch (JsonException ex)
            {
                // 손상된 히스토리를 빈 파일로 덮어쓰면 누적 기록 전체를 잃는다.
                Console.WriteLine($"::error::{historyFileName} 파싱 실패, 히스토리 갱신을 건너뜁니다: {ex.Message}");
                return;
            }
        }

        if (db.LastUpdate != null)
            history.LastUpdate[db.CollectDate] = db.LastUpdate;

        foreach (var (serverName, rankings) in db.Servers)
        {
            if (!history.Servers.TryGetValue(serverName, out var characters))
            {
                characters = new Dictionary<string, Dictionary<string, int>>();
                history.Servers[serverName] = characters;
            }

            for (int cc = 0; cc < CharacterNames.Length; cc++)
            {
                if (!characters.TryGetValue(CharacterNames[cc], out var dates))
                {
                    dates = new Dictionary<string, int>();
                    characters[CharacterNames[cc]] = dates;
                }
                dates[db.CollectDate] = rankings.Count(r => r.CharacterCode == cc);
            }
        }

        await File.WriteAllTextAsync(historyPath, JsonSerializer.Serialize(history, jsonOptions), Encoding.UTF8);
        Console.WriteLine($"히스토리 저장 위치: {Path.GetFullPath(historyPath)}");
    }

    // 사이트 값이 마지막 수집분과 다르면 새 갱신으로 본다.
    // 수집 기록이 없을 때는(첫 실행) 오늘 날짜의 갱신만 새 것으로 인정해, 어제 데이터를 오늘 것으로 담지 않게 한다.
    private static bool IsNewUpdate(string? siteValue, string? storedValue)
    {
        if (string.IsNullOrEmpty(siteValue)) return false;
        if (storedValue == null) return siteValue.StartsWith(NowKst.ToString("yyyy-MM-dd"));
        return siteValue != storedValue;
    }

    private static async Task<string?> ReadSiteLastUpdateAsync(IPage page)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await page.GotoAsync(LastUpdateProbeUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60000
                });
                var dd = await page.WaitForSelectorAsync(LastUpdateSelector, new PageWaitForSelectorOptions { Timeout = 20000 });
                string text = dd == null ? "" : (await dd.InnerTextAsync()).Trim();
                if (Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$")) return text;
                Console.WriteLine($" - Last Update 형식이 예상과 다릅니다: '{text}'");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($" - Last Update 읽기 실패 ({attempt}/2): {ex.GetType().Name}");
                if (attempt < 2) await Task.Delay(5000);
            }
        }
        return null;
    }

    // 직전 수집 파일의 LastUpdate. 필드가 없는 옛 파일이거나 파일이 없으면 null.
    private static string? ReadStoredLastUpdate(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            // 파일은 UTF-8 BOM으로 저장되므로 바이트가 아니라 문자열(BOM 제거됨)로 파싱한다.
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true });
            return doc.RootElement.TryGetProperty("LastUpdate", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"::warning::{Path.GetFileName(path)}에서 LastUpdate를 읽지 못했습니다: {ex.Message}");
            return null;
        }
    }

    private static TimeZoneInfo FindKst()
    {
        foreach (var id in new[] { "Korea Standard Time", "Asia/Seoul" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST");
    }

    private static bool EnvFlag(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) && v >= 0 ? v : fallback;

    private static string ExtractId(string rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return "Unknown";
        var match = Regex.Match(rawText, @"\(([^)]+)\)");
        if (match.Success) return match.Groups[1].Value;
        return rawText.Split(' ').Last().Trim();
    }
}
