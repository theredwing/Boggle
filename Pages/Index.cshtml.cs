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
