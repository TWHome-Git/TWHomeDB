using Microsoft.Playwright;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

class Program
{
    public class EtaRankingDb
    {
        public string CollectDate { get; set; } = string.Empty;
        public Dictionary<string, List<RankingItem>> Servers { get; set; } = new();
    }

    // 서버 → 캐릭터 → 날짜 → 인원수 누적 기록
    public class EtaHistoryDb
    {
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

    public static async Task Main()
    {
        // 1. 환경 감지: GitHub Actions인지 확인
        bool isGithubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

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

        var db = new EtaRankingDb { CollectDate = DateTime.Now.ToString("yyyy-MM-dd") };
        var servers = new (int Sc, string Name)[]
        {
            (7, "하이아칸"),
            (16, "네냐플")
        };
        var emptyCodes = new List<string>();

        // 수집 로직
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

        // 2. 저장 경로 설정 (여기가 핵심 수정 부분입니다!)
        string fileName = "eta_ranking.json";
        string filePath;

        if (isGithubActions)
        {
            // GitHub Actions에서는 실행 위치(Working Directory)가 저장소 루트입니다.
            filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        }
        else
        {
            // 로컬(내 컴퓨터)에서는 프로젝트 폴더나 실행 폴더에 저장합니다.
            filePath = fileName;
        }

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        string jsonString = JsonSerializer.Serialize(db, jsonOptions);

        // 지정된 경로에 파일 쓰기
        await File.WriteAllTextAsync(filePath, jsonString, Encoding.UTF8);
        Console.WriteLine($"최종 파일 저장 위치: {Path.GetFullPath(filePath)}");

        // 3. 캐릭터별 인원수 히스토리 갱신 (기존 파일에 오늘 날짜를 누적)
        string historyFileName = "eta_history.json";
        string historyPath = isGithubActions
            ? Path.Combine(Directory.GetCurrentDirectory(), historyFileName)
            : historyFileName;

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

    private static string ExtractId(string rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return "Unknown";
        var match = Regex.Match(rawText, @"\(([^)]+)\)");
        if (match.Success) return match.Groups[1].Value;
        return rawText.Split(' ').Last().Trim();
    }
}