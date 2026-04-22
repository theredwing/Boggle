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
            if (grid.Length == 25)
            {
                for (int r = 0; r < 5; r++)
                {
                    var row = new string[5];
                    for (int c = 0; c < 5; c++) row[c] = grid[r * 5 + c] ?? string.Empty;
                    GridRows.Add(row);
                }
            }

            // Validate saved words for adjacency
            var charGrid = new string[5,5];
            for (int r = 0; r < 5; r++) for (int c = 0; c < 5; c++) charGrid[r, c] = (r*5+c < grid.Length) ? (grid[r*5+c] ?? string.Empty) : string.Empty;

            foreach (var kv in SavedWords)
            {
                var list = new List<WordResult>();
                foreach (var w in kv.Value ?? new string[0])
                {
                    var res = new WordResult { Word = w, IsValid = false, Path = null };
                    if (!string.IsNullOrWhiteSpace(w))
                    {
                        var path = FindPathForWord(w, charGrid);
                        if (path != null)
                        {
                            res.IsValid = true;
                            res.Path = path;
                        }
                    }
                    list.Add(res);
                }
                PlayerResults[kv.Key] = list;
            }

            // Mark duplicates across players (case-insensitive). If multiple players submitted the same word, mark as duplicate.
            var wordGroups = new Dictionary<string, List<WordResult>>();
            foreach (var kv in PlayerResults)
            {
                foreach (var wr in kv.Value)
                {
                    if (string.IsNullOrWhiteSpace(wr.Word)) continue;
                    var key = wr.Word.Trim().ToLowerInvariant();
                    if (!wordGroups.TryGetValue(key, out var container)) { container = new List<WordResult>(); wordGroups[key] = container; }
                    container.Add(wr);
                }
            }
            foreach (var g in wordGroups.Values)
            {
                if (g.Count > 1)
                {
                    foreach (var wr in g) wr.IsDuplicate = true;
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
            int rows = 5, cols = 5;
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
                // compare first character of cell to target[idx]
                if (cell[0] != target[idx]) return false;

                // consume this cell
                visited[r, c] = true;
                path.Add((r, c));

                if (idx == target.Length - 1) return true;

                // explore neighbors
                for (int dr = -1; dr <= 1; dr++)
                {
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int nr = r + dr, nc = c + dc;
                        if (dfs(nr, nc, idx + 1)) return true;
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
    }
}
