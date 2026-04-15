using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using System.Linq;

namespace Boggle.Pages
{
    public class ReviewModel : PageModel
    {
        // Key: player index (0-based), Value: words array
        public Dictionary<int, string[]> SavedWords { get; private set; } = new Dictionary<int, string[]>();

        public void OnGet()
        {
            // Copy the in-memory saved words from IndexModel
            SavedWords = IndexModel.SavedWords.OrderBy(k => k.Key)
                .ToDictionary(k => k.Key, v => v.Value ?? new string[0]);
        }
    }
}
