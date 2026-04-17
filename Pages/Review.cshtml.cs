using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using System.Linq;

namespace Boggle.Pages
{
    public class ReviewModel : PageModel
    {
        // Key: player index (0-based), Value: words array
        public Dictionary<int, string[]> SavedWords { get; private set; } = new Dictionary<int, string[]>();
        // Grid rows (5x5)
        public List<string[]> GridRows { get; private set; } = new List<string[]>();

        public void OnGet()
        {
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
        }
    }
}
