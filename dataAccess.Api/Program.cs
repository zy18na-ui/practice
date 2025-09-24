using dataAccess.Api.Services; // <-- correct for your OllamaEmbeddingProvider
using dataAccess.Planning;
using dataAccess.Services;
using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

Env.Load();

var builder = WebApplication.CreateBuilder(args);

var rel = Environment.GetEnvironmentVariable("APP__REL__CONNECTIONSTRING")
          ?? builder.Configuration["APP__REL__CONNECTIONSTRING"]
          ?? throw new Exception("APP__REL__CONNECTIONSTRING missing");

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(rel));

// Register the embedder as a typed HttpClient for the interface
builder.Services.AddHttpClient<IEmbeddingProvider, OllamaEmbeddingProvider>((sp, http) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["APP__EMBED__BASEADDRESS"]
                  ?? Environment.GetEnvironmentVariable("APP__EMBED__BASEADDRESS")
                  ?? "http://localhost:11434/";
    http.BaseAddress = new Uri(baseUrl);
});

builder.Services.AddHttpClient();
builder.Services.AddSingleton<Registry>(sp =>
{
    var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Planning", "SchemaRegistry.json"));
    return System.Text.Json.JsonSerializer.Deserialize<Registry>(json) ?? new Registry();
});
builder.Services.AddScoped<PlannerService>();
builder.Services.AddScoped<PlanValidator>();
builder.Services.AddScoped<PlanExecutor>();
builder.Services.AddScoped<SqlQueryService>();
builder.Services.AddScoped<HybridQueryService>();
builder.Services.AddScoped<VectorSearchService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var MyCors = "_myCors";
builder.Services.AddCors(o =>
{
    o.AddPolicy(MyCors, p => p
        .WithOrigins("http://localhost:5173") // your React dev URL
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }

app.UseCors(MyCors);

app.MapGet("/health", () => Results.Ok(new { ok = true }));

// --------------------------------
// SQL endpoints
// --------------------------------

// KEEP ONLY the DB-LIMIT version (you already had this good one)
app.MapGet("/api/sql/products", async (SqlQueryService svc, int? limit) =>
{
    var n = (limit is > 0) ? limit.Value : 50;
    var rows = await svc.GetProductsAsync(n);  // DB-level LIMIT
    return Results.Ok(rows);
});

app.MapGet("/api/sql/suppliers", async (SqlQueryService svc, string? q, int? limit) =>
{
    var n = (limit is > 0) ? limit!.Value : 50;
    var data = string.IsNullOrWhiteSpace(q)
        ? await svc.GetSuppliersAsync(n)
        : await svc.SearchSuppliersAsync(q!, n);
    return Results.Ok(data);
});

// categories list/search
app.MapGet("/api/sql/productcategory", async (SqlQueryService svc, string? q, int? limit) =>
{
    var n = (limit is > 0) ? limit!.Value : 50;
    var data = string.IsNullOrWhiteSpace(q)
        ? await svc.GetCategoriesAsync(n)
        : await svc.SearchCategoriesAsync(q!, n);
    return Results.Ok(data);
});

app.MapPost("/api/sql/route", async (SqlQueryService svc, RouteReq req, CancellationToken ct) =>
    Results.Ok(await svc.DispatchAsync(req.Input ?? "", ct)));

app.MapPost("/api/hybrid/route", async (HybridQueryService svc, RouteReq req, CancellationToken ct) =>
    Results.Ok(await svc.DispatchAsync(req.Input ?? "", ct)));

app.MapPost("/api/vector/route", async (VectorSearchService svc, RouteReq req, CancellationToken ct) =>
    Results.Ok(await svc.DispatchAsync(req.Input ?? "", ct)));

// --------------------------------
// Shared helper for chat routing
// --------------------------------
// FIX: normal method + normal switch (not a switch *expression*), so we can run multiple statements per arm
static async Task<IResult> HandleChatAsync(
    string input,
    SqlQueryService sqlSvc,
    HybridQueryService hybSvc,
    VectorSearchService vecSvc,
    CancellationToken ct)
{
    var lower = (input ?? "").Trim().ToLowerInvariant();

    // 1) explicit prefixes
    if (lower.StartsWith("sql:"))
    {
        var q = input.Substring("sql:".Length).Trim();
        var res = await sqlSvc.DispatchAsync(q, ct);
        return Results.Json(new { route = "sql", query = q, result = res });
    }
    if (lower.StartsWith("hybrid:"))
    {
        var q = input.Substring("hybrid:".Length).Trim();
        var res = await hybSvc.DispatchAsync(q, ct);
        return Results.Json(new { route = "hybrid", query = q, result = res });
    }
    if (lower.StartsWith("vector:"))
    {
        var q = input.Substring("vector:".Length).Trim();
        var res = await vecSvc.DispatchAsync(q, ct);
        return Results.Json(new { route = "vector", query = q, result = res });
    }

    // 2) exact SQL shortcuts
    switch (lower)
    {
        case "all products":
        case "all suppliers":
        case "all categories":
            {
                var res = await sqlSvc.DispatchAsync(lower, ct);
                return Results.Json(new { route = "sql", query = lower, result = res });
            }
    }
    if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"^order\s+\d+$"))
    {
        var res = await sqlSvc.DispatchAsync(input, ct);
        return Results.Json(new { route = "sql", query = input, result = res });
    }

    // 3) Groq classification / fallback
    var groqKey = Environment.GetEnvironmentVariable("APP__GROQ__API_KEY");
    if (string.IsNullOrWhiteSpace(groqKey))
    {
        return Results.Json(new
        {
            route = "none",
            input,
            note = "Set APP__GROQ__API_KEY to enable classification. You can still use prefixes: sql:/hybrid:/vector:."
        });
    }

    // TIP: if raw string """ causes trouble with your LangVersion, change to a normal string.
    var sys = """
        You are a router. Classify the user's request into exactly one of:
        "sql" | "hybrid" | "vector" | "other".
        Output ONLY JSON: { "route": "...", "query": "..." }.
        For sql/hybrid/vector, format query for our dispatchers:
        sql: "all products" | "all suppliers" | "all categories" | "suppliers: <text>" | "categories: <text>" | "order <id>"
        hybrid/vector: "products|suppliers|categories: <text>" (vector may include "topk:<n>")
        """;

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("Authorization", "Bearer " + groqKey);

    var classifyReq = new
    {
        model = "gemma2-9b-it",
        response_format = new { type = "json_object" },
        messages = new[]
        {
            new { role = "system", content = sys },
            new { role = "user", content = input }
        }
    };

    var classifyJson = System.Text.Json.JsonSerializer.Serialize(classifyReq);
    using var classifyResp = await http.PostAsync(
        "https://api.groq.com/openai/v1/chat/completions",
        new StringContent(classifyJson, System.Text.Encoding.UTF8, "application/json"),
        ct);

    if (!classifyResp.IsSuccessStatusCode)
    {
        var err = await classifyResp.Content.ReadAsStringAsync(ct);
        return Results.Problem($"Groq classify failed: {err}");
    }

    var classifyText = await classifyResp.Content.ReadAsStringAsync(ct);
    using var doc = System.Text.Json.JsonDocument.Parse(classifyText);
    var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";

    using var routed = System.Text.Json.JsonDocument.Parse(content);
    var route = routed.RootElement.GetProperty("route").GetString() ?? "other";
    var query = routed.RootElement.GetProperty("query").GetString() ?? input;

    // normal switch so we can do work in each arm
    switch (route)
    {
        case "sql":
            {
                var res = await sqlSvc.DispatchAsync(query, ct);
                return Results.Json(new { route, query, result = res });
            }
        case "hybrid":
            {
                var res = await hybSvc.DispatchAsync(query, ct);
                return Results.Json(new { route, query, result = res });
            }
        case "vector":
            {
                var res = await vecSvc.DispatchAsync(query, ct);
                return Results.Json(new { route, query, result = res });
            }
        default:
            {
                // freeform chat answer
                var chatReq = new
                {
                    model = "gemma2-9b-it",
                    messages = new[]
                    {
                    new { role = "system", content = "You are a helpful assistant." },
                    new { role = "user", content = input }
                }
                };
                var chatJson = System.Text.Json.JsonSerializer.Serialize(chatReq);
                using var chatResp = await http.PostAsync(
                    "https://api.groq.com/openai/v1/chat/completions",
                    new StringContent(chatJson, System.Text.Encoding.UTF8, "application/json"),
                    ct);
                var chatText = await chatResp.Content.ReadAsStringAsync(ct);
                var reply = System.Text.Json.JsonDocument.Parse(chatText)
                             .RootElement.GetProperty("choices")[0]
                             .GetProperty("message").GetProperty("content").GetString();
                return Results.Json(new { route = "other", input, answer = reply });
            }
    }
}


// --------------------------------
// Chat routes
// --------------------------------

// FIX: a working GET version (no fake delegate). lets you test quickly via ?q=...
app.MapGet("/api/chat/route", async (
    string q,
    SqlQueryService sqlSvc,
    HybridQueryService hybSvc,
    VectorSearchService vecSvc,
    CancellationToken ct) =>
{
    return await HandleChatAsync(q ?? "", sqlSvc, hybSvc, vecSvc, ct);
});

// FIX: safer POST parsing, then call the same helper
app.MapPost("/api/chat/route", async (
    HttpContext ctx,
    SqlQueryService sqlSvc,
    HybridQueryService hybSvc,
    VectorSearchService vecSvc,
    CancellationToken ct) =>
{
    string input = "";
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var raw = await reader.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            using var jdoc = System.Text.Json.JsonDocument.Parse(raw);
            if (jdoc.RootElement.TryGetProperty("input", out var inp) &&
                inp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                input = inp.GetString() ?? "";
            }
        }
    }
    catch
    {
        // ignore malformed JSON; input stays ""
    }

    return await HandleChatAsync(input, sqlSvc, hybSvc, vecSvc, ct);
});
app.MapPost("/api/plan/execute", async (
    dataAccess.Planning.PlannerService planner,
    dataAccess.Planning.PlanValidator validator,
    dataAccess.Planning.PlanExecutor exec,
    HttpContext ctx,
    CancellationToken ct) =>
{
    using var jdoc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
    var root = jdoc.RootElement;

    dataAccess.Planning.QueryPlan plan;

    // Explicit plan: top-level "plan": [ ... ]
    if (root.TryGetProperty("plan", out var planEl) && planEl.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        plan = new dataAccess.Planning.QueryPlan { Plan = new List<object>() };

        // Detach each step from 'jdoc' so it survives response serialization
        foreach (var step in planEl.EnumerateArray())
            plan.Plan.Add(step.Clone());
    }
    else
    {
        // Natural language → call planner
        var input = root.TryGetProperty("input", out var inp) && inp.ValueKind == System.Text.Json.JsonValueKind.String
            ? inp.GetString() ?? ""
            : "";
        plan = await planner.PlanAsync(input, ct);
    }

    // Normalize both paths: ensure every step is a JsonElement for PlanExecutor
    if (plan?.Plan != null)
    {
        var normalized = new List<object>(plan.Plan.Count);
        foreach (var step in plan.Plan)
        {
            if (step is JsonElement je)
            {
                normalized.Add(je.Clone()); // safe copy
            }
            else
            {
                // convert anonymous objects / dictionaries into JsonElement
                var el = System.Text.Json.JsonSerializer.SerializeToElement(step);
                normalized.Add(el);
            }
        }
        plan.Plan = normalized;
    }

    var (ok, err, fixedPlan) = validator.Validate(plan);
    if (!ok) return Results.BadRequest(new { error = err });

    var data = await exec.ExecuteAsync(fixedPlan, ct);
    return Results.Ok(new { plan = fixedPlan, result = data });
});

app.Run();

public sealed class RouteReq { public string? Input { get; set; } }
