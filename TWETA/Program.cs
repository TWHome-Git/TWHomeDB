using Microsoft.Playwright;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

class Program
{
    public class EtaRankingDb
    {
        public string CollectDate { get; set; } = string.Empty;
        public List<RankingItem> Rankings { get; set; } = new();
    }

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

        // 수집 로직 (기존과 동일)
        for (int cc = 0; cc <= 18; cc++)
        {
            Console.WriteLine($"캐릭터 코드 {cc} 수집 시작...");
            for (int p = 1; p <= 50; p++)
            {
                string url = $"https://tales.nexon.com/Community/Ranking/EtaRank?sc=7&cc={cc}&pagesize=100&page={p}";
                try
                {
                    await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                    await page.WaitForSelectorAsync("table tbody tr", new PageWaitForSelectorOptions { Timeout = 10000 });
                }
                catch
                {
                    Console.WriteLine($" - {p}페이지 로딩 실패 또는 데이터 없음");
                    break;
                }

                var rows = await page.QuerySelectorAllAsync("table tbody tr");
                if (rows.Count == 0 || (await rows[0].InnerTextAsync()).Contains("데이터가 없습니다")) break;

                foreach (var row in rows)
                {
                    var cols = await row.QuerySelectorAllAsync("td");
                    if (cols.Count >= 4)
                    {
                        string rankText = (await cols[0].InnerTextAsync()).Trim();
                        string nameText = (await cols[1].InnerTextAsync()).Trim();
                        string levelText = (await cols[2].InnerTextAsync()).Trim();
                        string essenceText = (await cols[3].InnerTextAsync()).Trim().Replace(",", "");

                        db.Rankings.Add(new RankingItem
                        {
                            CharacterCode = cc,
                            Rank = int.TryParse(rankText, out int r) ? r : 0,
                            UserId = ExtractId(nameText),
                            Level = int.TryParse(levelText, out int l) ? l : 0,
                            Essence = long.TryParse(essenceText, out long e) ? e : 0
                        });
                    }
                }
                Console.WriteLine($" - {p}페이지 완료 (누적 {db.Rankings.Count}명)");
                if (rows.Count < 100) break;
                await Task.Delay(500);
            }
        }

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
    }

    private static string ExtractId(string rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return "Unknown";
        var match = Regex.Match(rawText, @"\(([^)]+)\)");
        if (match.Success) return match.Groups[1].Value;
        return rawText.Split(' ').Last().Trim();
    }
}