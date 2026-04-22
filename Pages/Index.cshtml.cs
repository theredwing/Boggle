using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;

namespace Boggle.Pages
{
    public class IndexModel: PageModel
    {
        // In-memory storage for demo purposes. In production persist to database.
        internal static ConcurrentDictionary<int, string[]> SavedWords { get; } = new ConcurrentDictionary<int, string[]>();
        // Stored grid snapshot (flattened 25 elements) and lock for thread-safety
        internal static string[] SavedGrid { get; private set; } = new string[0];
        private static readonly object GridLock = new object();

        public void OnGet ()
        {

        }

        // AJAX endpoint: POST /Index?handler=CleanWords
        // Clears all SavedWords so PlayerResults will be empty on Review page
        public IActionResult OnPostCleanWords()
        {
            SavedWords.Clear();
            lock (GridLock)
            {
                SavedGrid = new string[0];
            }
            return new JsonResult(new { success = true });
        }

        // AJAX endpoint: POST /Index?handler=SaveGrid
        public async Task<IActionResult> OnPostSaveGridAsync()
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body)) return new JsonResult(new { success = false, error = "empty body" });

            try
            {
                var dto = JsonSerializer.Deserialize<SaveGridDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (dto?.Grid != null)
                {
                    // expect 25 values for 5x5
                    if (dto.Grid.Length != 25) return new JsonResult(new { success = false, error = "grid must have 25 elements" });
                    lock (GridLock)
                    {
                        SavedGrid = dto.Grid.ToArray();
                    }
                    return new JsonResult(new { success = true });
                }
                return new JsonResult(new { success = false, error = "invalid payload" });
            }
            catch (JsonException je)
            {
                return new JsonResult(new { success = false, error = je.Message });
            }
        }

        private class SaveGridDto
        {
            public string[] Grid { get; set; }
        }

        // AJAX endpoint: POST /Index?handler=ValidateGrid
        // Accepts a grid (25 strings) and returns validation results for SavedWords
        public async Task<IActionResult> OnPostValidateGridAsync()
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body)) return new JsonResult(new { success = false, error = "empty body" });

            try
            {
                var dto = JsonSerializer.Deserialize<SaveGridDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (dto?.Grid == null || dto.Grid.Length != 25) return new JsonResult(new { success = false, error = "grid must have 25 elements" });

                // build grid 5x5
                var grid = new string[5, 5];
                for (int r = 0; r < 5; r++) for (int c = 0; c < 5; c++) grid[r, c] = dto.Grid[r * 5 + c] ?? string.Empty;

                // validate saved words
                var results = new Dictionary<int, List<object>>();
                foreach (var kv in SavedWords.OrderBy(k => k.Key))
                {
                    var list = new List<object>();
                    foreach (var w in kv.Value ?? new string[0])
                    {
                        var path = FindPathForWord(w ?? string.Empty, grid);
                        if (path != null)
                        {
                            list.Add(new { word = w, isValid = true, path = path.Select(p => new int[] { p.r, p.c }).ToArray() });
                        }
                        else
                        {
                            list.Add(new { word = w, isValid = false, path = new int[0][] });
                        }
                    }
                    results[kv.Key] = list;
                }

                return new JsonResult(new { success = true, playerResults = results });
            }
            catch (JsonException je)
            {
                return new JsonResult(new { success = false, error = je.Message });
            }
        }

        // Reuse DFS path finder for validation (returns list of (r,c) pairs or null)
        private List<(int r, int c)> FindPathForWord(string word, string[,] grid)
        {
            if (string.IsNullOrWhiteSpace(word)) return null;
            var target = word.Trim().ToLowerInvariant();
            int rows = 5, cols = 5;
            bool[,] visited = new bool[rows, cols];
            List<(int r, int c)> path = new List<(int r, int c)>();

            bool dfs(int r, int c, int idx)
            {
                if (idx >= target.Length) return true;
                if (r < 0 || r >= rows || c < 0 || c >= cols) return false;
                if (visited[r, c]) return false;
                var cell = (grid[r, c] ?? string.Empty).ToLowerInvariant();
                if (cell.Length == 0) return false;
                if (cell[0] != target[idx]) return false;

                visited[r, c] = true;
                path.Add((r, c));
                if (idx == target.Length - 1) return true;

                for (int dr = -1; dr <= 1; dr++)
                {
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        if (dfs(r + dr, c + dc, idx + 1)) return true;
                    }
                }

                visited[r, c] = false;
                path.RemoveAt(path.Count - 1);
                return false;
            }

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    for (int i = 0; i < rows; i++) for (int j = 0; j < cols; j++) visited[i, j] = false;
                    path.Clear();
                    if (dfs(r, c, 0)) return new List<(int r, int c)>(path);
                }
            }
            return null;
        }

        // AJAX endpoint: POST /Index?handler=SaveWords
        public async Task<IActionResult> OnPostSaveWordsAsync()
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body)) return new JsonResult(new { success = false, error = "empty body" });

            try
            {
                var dto = JsonSerializer.Deserialize<SaveWordsDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (dto?.Words != null)
                {
                    SavedWords[dto.PlayerIndex] = dto.Words.ToArray();
                    return new JsonResult(new { success = true });
                }
                return new JsonResult(new { success = false, error = "invalid payload" });
            }
            catch (JsonException je)
            {
                return new JsonResult(new { success = false, error = je.Message });
            }
        }

        private class SaveWordsDto
        {
            public int PlayerIndex { get; set; }
            public string[] Words { get; set; }
        }
    }
}
