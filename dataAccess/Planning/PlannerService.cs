using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace dataAccess.Planning;

public sealed class PlannerService
{
    private readonly HttpClient _http;
    private readonly string _groqKey;
    private readonly string _registryJson;

    public PlannerService(IHttpClientFactory factory, IConfiguration cfg)
    {
        _http = factory.CreateClient();
        _groqKey = Environment.GetEnvironmentVariable("APP__GROQ__API_KEY") ?? "";
        _registryJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Planning", "SchemaRegistry.json"));
    }

    public async Task<QueryPlan> PlanAsync(string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new QueryPlan { Plan = new List<object>() };

        // If no key, skip straight to fallback
        if (string.IsNullOrWhiteSpace(_groqKey))
            return MakeHeuristicFallback(input);

        // Build request
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _groqKey);

        var systemPrompt =
            "You are a planner. Output ONLY strict JSON with shape {\"plan\":[ ... ]}. " +
            "Allowed ops: vector_search, select, join, aggregate. " +
            "Use entities from the given registry. " +
            "If user asks about price, use productcategory.price (not product). " +
            "Sort using select.sort: [{\"field\":\"price\",\"dir\":\"asc|desc\"}]. " +
            "Use ids_in to pass values between steps. Do not include commentary.";

        var payload = new
        {
            model = "gemma2-9b-it",
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = $"REGISTRY:\n{_registryJson}\n\nUSER:\n{input}" }
            },
            temperature = 0.1,
            response_format = new { type = "json_object" }
        };

        try
        {
            var resp = await _http.PostAsJsonAsync(
                "https://api.groq.com/openai/v1/chat/completions", payload, ct);

            if (!resp.IsSuccessStatusCode)
                return MakeHeuristicFallback(input);

            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
                return MakeHeuristicFallback(input);

            var plan = JsonSerializer.Deserialize<QueryPlan>(content);
            if (plan?.Plan is { Count: > 0 })
                return plan;

            return MakeHeuristicFallback(input);
        }
        catch
        {
            return MakeHeuristicFallback(input);
        }
    }

    // ===== Heuristic fallback =====
    private QueryPlan MakeHeuristicFallback(string userInput)
    {
        var text = userInput ?? string.Empty;
        var lower = text.ToLowerInvariant();

        var searchText = ExtractSearchText(text);

        bool wantSupplier = lower.Contains("supplier");
        bool wantExpensive = lower.Contains("most expensive") || lower.Contains("expensive") || lower.Contains("highest");
        bool wantCheapest = lower.Contains("cheapest") || lower.Contains("lowest") || lower.Contains("low price");

        int limit = TryExtractTopN(lower) ?? (wantSupplier ? 5 : 1);

        if (wantSupplier)
        {
            return new QueryPlan
            {
                Plan = new List<object>
                {
                    new { op = "vector_search", entity = "product", text = searchText, topk = 10, @return = "ids" },
                    new { op = "select", entity = "supplier", ids_in = "ids",
                          sort = new[] { new { field = "name", dir = "asc" } }, limit = limit }
                }
            };
        }

        var dir = wantExpensive ? "desc" : "asc"; // default to cheapest if unclear
        return new QueryPlan
        {
            Plan = new List<object>
            {
                new { op = "vector_search", entity = "product", text = searchText, topk = 10, @return = "ids" },
                new { op = "select", entity = "productcategory", ids_in = "ids",
                      sort = new[] { new { field = "price", dir = dir } }, limit = limit }
            }
        };
    }

    // Extract the product search phrase from a natural sentence.
    // Examples:
    //  "cheapest dino onesie"         -> "dino onesie"
    //  "most expensive apple strap"   -> "apple strap"
    //  "list the suppliers for X"     -> "x"
    //  "top 3 cheapest dino onesie"   -> "dino onesie"
    private static string ExtractSearchText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var text = raw.Trim();

        // Prefer quoted phrase if present: "dino onesie"
        var q = Regex.Match(text, "\"([^\"]+)\"");
        if (q.Success) return q.Groups[1].Value.Trim();

        var lower = text.ToLowerInvariant();

        // Common patterns: "... for <phrase>"
        var forMatch = Regex.Match(lower, @"\bfor\s+(.+)$");
        if (forMatch.Success)
        {
            var afterFor = forMatch.Groups[1].Value;
            return CleanTokens(afterFor);
        }

        // Remove directive-like tokens and keep last 3-5 keywords
        return CleanTokens(lower);
    }

    private static string CleanTokens(string s)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cheapest","most","expensive","lowest","highest","price","cost","list","show","find",
        "me","the","a","an","of","for","under","over","top","items","item","products","product",
        "supplier","suppliers","with","and","or","to","please"
    };

        // normalize and split
        var cleaned = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9\s-]", " ").Trim();
        var tokens = cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        // drop pure-number tokens anywhere
        tokens = tokens.Where(t => !System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+$")).ToList();

        // remove stopwords
        tokens = tokens.Where(t => !stop.Contains(t)).ToList();

        if (tokens.Count == 0) return cleaned;

        // keep last 2..5 tokens (product words often at the tail)
        var take = Math.Min(Math.Max(tokens.Count, 2), 5);
        var slice = tokens.Skip(Math.Max(0, tokens.Count - take)).Take(take);

        var phrase = string.Join(" ", slice).Trim();

        // final guard: if phrase starts with a number, strip it
        phrase = System.Text.RegularExpressions.Regex.Replace(phrase, @"^\d+\s*", "");

        return string.IsNullOrWhiteSpace(phrase) ? cleaned : phrase;
    }


    private static int? TryExtractTopN(string lower)
    {
        // "top 3", "3 cheapest", "3 most expensive"
        var m = Regex.Match(lower, @"\btop\s*(\d+)|\b(\d+)\s*(cheapest|expensive|items?)");
        if (m.Success)
        {
            for (int i = 1; i < m.Groups.Count; i++)
                if (int.TryParse(m.Groups[i].Value, out var n) && n > 0 && n <= 50)
                    return n;
        }

        var words = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["one"] = 1,
            ["two"] = 2,
            ["three"] = 3,
            ["four"] = 4,
            ["five"] = 5,
            ["six"] = 6,
            ["seven"] = 7,
            ["eight"] = 8,
            ["nine"] = 9,
            ["ten"] = 10
        };
        foreach (var kv in words)
            if (lower.Contains($"top {kv.Key}") || Regex.IsMatch(lower, $@"\b{kv.Key}\b"))
                return kv.Value;

        return null;
    }
}
