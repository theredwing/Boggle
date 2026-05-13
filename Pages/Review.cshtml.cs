using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System;

namespace Boggle.Pages
{
    public class ReviewModel : PageModel
    {
        // Key: player index (0-based), Value: words array
        public Dictionary<int, string[]> SavedWords { get; private set; } = new Dictionary<int, string[]>();
        // Grid rows (5x5)
        public List<string[]> GridRows { get; private set; } = new List<string[]>();
        // Validation results per player
        public class WordResult
        {
            public string Word { get; set; }
            public bool IsValid { get; set; }
            // list of (row,col) 0-based coordinates for the path if found
            public List<(int r, int c)> Path { get; set; }
            public bool IsDuplicate { get; set; }
            public string Definition { get; set; }
            public int Score { get; set; }
        }

        // AJAX endpoint: POST /Review?handler=CleanWords
        // Removes empty/whitespace words from SavedWords
        public IActionResult OnPostCleanWords()
        {
            foreach (var key in IndexModel.SavedWords.Keys.ToList())
            {
                if (IndexModel.SavedWords.TryGetValue(key, out var arr))
                {
                    var filtered = (arr ?? new string[0]).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                    IndexModel.SavedWords[key] = filtered;
                }
            }
            return new JsonResult(new { success = true });
        }

        public Dictionary<int, List<WordResult>> PlayerResults { get; private set; } = new Dictionary<int, List<WordResult>>();
        public Dictionary<int, int> PlayerScores { get; private set; } = new Dictionary<int, int>();

        public async Task OnGetAsync()
        {
            // Prevent browser caching so cleared results are reflected
            Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";

            // Copy the in-memory saved words from IndexModel
            SavedWords = IndexModel.SavedWords.OrderBy(k => k.Key)
                .ToDictionary(k => k.Key, v => v.Value ?? new string[0]);

            // Copy saved grid snapshot
            var grid = IndexModel.SavedGrid ?? new string[0];
            GridRows = new List<string[]>();

            // Support both 5x5 (25) and 6x6 (36) grids
            int gridSize = 0;
            if (grid.Length == 25) gridSize = 5;
            else if (grid.Length == 36) gridSize = 6;

            if (gridSize > 0)
            {
                for (int r = 0; r < gridSize; r++)
                {
                    var row = new string[gridSize];
                    for (int c = 0; c < gridSize; c++) row[c] = grid[r * gridSize + c] ?? string.Empty;
                    GridRows.Add(row);
                }
            }

            // Validate saved words for adjacency
            var charGrid = new string[gridSize, gridSize];
            for (int r = 0; r < gridSize; r++) 
                for (int c = 0; c < gridSize; c++) 
                    charGrid[r, c] = (r * gridSize + c < grid.Length) ? (grid[r * gridSize + c] ?? string.Empty) : string.Empty;

            foreach (var kv in SavedWords)
            {
                var list = new List<WordResult>();
                var seenWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var w in kv.Value ?? new string[0])
                {
                    var res = new WordResult { Word = w, IsValid = false, Path = null };
                    if (!string.IsNullOrWhiteSpace(w))
                    {
                        var normalizedWord = w.Trim().ToLowerInvariant();

                        // Check if this player already entered this word
                        if (seenWords.Contains(normalizedWord))
                        {
                            // Mark as duplicate within same player (will be crossed off)
                            res.IsDuplicate = true;
                        }
                        else
                        {
                            // First occurrence - validate it
                            seenWords.Add(normalizedWord);
                            var path = FindPathForWord(w, charGrid);
                            if (path != null)
                            {
                                res.IsValid = true;
                                res.Path = path;
                            }
                        }
                    }
                    list.Add(res);
                }
                PlayerResults[kv.Key] = list;
            }

            // Mark duplicates across players (case-insensitive). If multiple players submitted the same word, mark as duplicate.
            // This marks words that appear in DIFFERENT players' lists.
            var wordGroups = new Dictionary<string, List<(int playerIndex, WordResult result)>>();
            foreach (var kv in PlayerResults)
            {
                foreach (var wr in kv.Value)
                {
                    if (string.IsNullOrWhiteSpace(wr.Word)) continue;
                    var key = wr.Word.Trim().ToLowerInvariant();
                    if (!wordGroups.TryGetValue(key, out var container)) 
                    { 
                        container = new List<(int playerIndex, WordResult result)>(); 
                        wordGroups[key] = container; 
                    }
                    container.Add((kv.Key, wr));
                }
            }
            foreach (var g in wordGroups.Values)
            {
                // Get unique player indices for this word
                var uniquePlayers = g.Select(x => x.playerIndex).Distinct().ToList();

                // Only mark as duplicate if multiple DIFFERENT players entered this word
                if (uniquePlayers.Count > 1)
                {
                    foreach (var item in g) 
                    {
                        item.result.IsDuplicate = true;
                    }
                }
            }

            // Fetch definitions for valid words from dictionary API
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            foreach (var kv in PlayerResults)
            {
                foreach (var wr in kv.Value)
                {
                    if (wr.IsValid && !string.IsNullOrWhiteSpace(wr.Word))
                    {
                        var def = await FetchDefinition(httpClient, wr.Word);
                        if (string.IsNullOrEmpty(def))
                        {
                            // word not in dictionary - mark invalid
                            wr.IsValid = false;
                            wr.Path = null;
                        }
                        else
                        {
                            wr.Definition = def;
                        }
                    }
                }
            }

            // Calculate scores for each word and total for each player
            foreach (var kv in PlayerResults)
            {
                int totalScore = 0;
                foreach (var wr in kv.Value)
                {
                    // Only score valid words that are not duplicates
                    if (wr.IsValid && !wr.IsDuplicate)
                    {
                        wr.Score = CalculateScore(wr.Word);
                        totalScore += wr.Score;
                    }
                    else
                    {
                        wr.Score = 0;
                    }
                }
                PlayerScores[kv.Key] = totalScore;
            }
        }

        private async Task<string> FetchDefinition(HttpClient client, string word)
        {
            try
            {
                var url = $"https://api.dictionaryapi.dev/api/v2/entries/en/{Uri.EscapeDataString(word.ToLowerInvariant())}";
                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                // Extract first definition from first meaning
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var entry = doc.RootElement[0];
                    if (entry.TryGetProperty("meanings", out var meanings) && meanings.ValueKind == JsonValueKind.Array && meanings.GetArrayLength() > 0)
                    {
                        var meaning = meanings[0];
                        if (meaning.TryGetProperty("definitions", out var defs) && defs.ValueKind == JsonValueKind.Array && defs.GetArrayLength() > 0)
                        {
                            var def = defs[0];
                            if (def.TryGetProperty("definition", out var defText))
                            {
                                return defText.GetString();
                            }
                        }
                    }
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        // Find path for word; returns list of (r,c) 0-based coordinates or null if not found
        private List<(int r, int c)> FindPathForWord(string word, string[,] grid)
        {
            if (string.IsNullOrWhiteSpace(word)) return null;
            var w = word.Trim();

            // Determine grid size dynamically
            int rows = grid.GetLength(0);
            int cols = grid.GetLength(1);

            // normalize to single characters lower-case for comparison
            var target = w.ToLowerInvariant();

            bool[,] visited = new bool[rows, cols];

            List<(int r, int c)> path = new List<(int r, int c)>();

            bool dfs(int r, int c, int idx)
            {
                if (idx >= target.Length) return true;
                if (r < 0 || r >= rows || c < 0 || c >= cols) return false;
                if (visited[r, c]) return false;
                var cell = (grid[r, c] ?? string.Empty).ToLowerInvariant();
                if (cell.Length == 0) return false;

                // Handle "Qu" square - it contributes both 'q' and 'u' to the path
                int cellLength = cell.Length;

                // Check if the cell matches the required sequence in the target word
                if (idx + cellLength > target.Length) return false;

                // Compare all characters in the cell with the target sequence
                for (int i = 0; i < cellLength; i++)
                {
                    if (cell[i] != target[idx + i]) return false;
                }

                // consume this cell
                visited[r, c] = true;
                path.Add((r, c));

                // Move index forward by the number of characters this cell contributes
                int nextIdx = idx + cellLength;

                if (nextIdx >= target.Length) return true;

                // explore neighbors
                for (int dr = -1; dr <= 1; dr++)
                {
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int nr = r + dr, nc = c + dc;
                        if (dfs(nr, nc, nextIdx)) return true;
                    }
                }

                // backtrack
                visited[r, c] = false;
                path.RemoveAt(path.Count - 1);
                return false;
            }

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    // reset visited and path for each start
                    for (int i = 0; i < rows; i++) for (int j = 0; j < cols; j++) visited[i, j] = false;
                    path.Clear();
                    if (dfs(r, c, 0)) return new List<(int r, int c)>(path);
                }
            }

            return null;
        }

        private int CalculateScore(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return 0;

            int length = word.Trim().Length;

            // Scoring based on word length
            return length switch
            {
                4 => 1,
                5 => 2,
                6 => 3,
                7 => 5,
                8 => 8,
                9 => 12,
                10 => 17,
                11 => 23,
                12 => 30,
                13 => 38,
                14 => 47,
                15 => 57,
                16 => 68,
                17 => 80,
                18 => 93,
                19 => 107,
                20 => 122,
                21 => 138,
                22 => 155,
                23 => 173,
                24 => 192,
                25 => 212,
                26 => 233,
                27 => 255,
                28 => 278,
                29 => 302,
                _ => 0 // Less than 4 or more than 29
            };
        }
    }
}
