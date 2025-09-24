using System.Text.Json;
using dataAccess.Services;
using System.Linq;
using Shared.DTOs;

namespace dataAccess.Planning;

public sealed class ProductWithPriceDto
{
    public int ProductId { get; set; }
    public string? ProductName { get; set; }
    public string? Description { get; set; }  // maps from ProductDto.ProductDescription
    public string? ImageUrl { get; set; }
    public int? SupplierId { get; set; }
    public decimal? Price { get; set; }
    public decimal? Cost { get; set; }
    public int? ProductCategoryId { get; set; }
}

public sealed class PlanExecutor
{
    private readonly SqlQueryService _sql;
    private readonly VectorSearchService _vec;

    public PlanExecutor(SqlQueryService sql, VectorSearchService vec)
    {
        _sql = sql;
        _vec = vec;
    }

    public async Task<object> ExecuteAsync(QueryPlan plan, CancellationToken ct)
    {
        // tiny variable table to pass values between ops
        var vars = new Dictionary<string, object?>();
        Console.WriteLine($"[plan] {System.Text.Json.JsonSerializer.Serialize(plan)}");

        if (plan?.Plan is null || plan.Plan.Count == 0)
            return Array.Empty<object>();

        foreach (var stepObj in plan.Plan)
        {
            // The plan holds objects; we rehydrate via JsonElement and inspect "op"/"Op"
            if (stepObj is not JsonElement je || je.ValueKind != JsonValueKind.Object)
                continue;

            string? opType = null;
            if (je.TryGetProperty("Op", out var opProp)) opType = opProp.GetString();
            if (opType is null && je.TryGetProperty("op", out var opProp2)) opType = opProp2.GetString();

            switch (opType)
            {
                case "vector_search":
                    {
                        var entity = je.GetProperty("entity").GetString() ?? "";
                        var text = je.GetProperty("text").GetString() ?? "";
                        var topk = je.TryGetProperty("topk", out var kEl) && kEl.ValueKind == JsonValueKind.Number ? kEl.GetInt32() : 10;
                        var retKey = je.TryGetProperty("return", out var rEl) ? (rEl.GetString() ?? "ids") : "ids";

                        if (entity.Equals("product", StringComparison.OrdinalIgnoreCase))
                        {
                            // OPTION A (already used in your file): ANN → List<int> product IDs
                            var ids = await _vec.SearchProductIdsAsync(text, topk, null, ct);
                            Console.WriteLine($"[PlanExecutor] vector ids for '{text}': {string.Join(",", ids)}");
                            vars[retKey] = ids;
                        }
                        else
                        {
                            vars[retKey] = new List<int>();
                        }
                        break;
                    }

                case "select":
                    {
                        var entity = je.GetProperty("entity").GetString() ?? "";
                        var idsInVar = je.TryGetProperty("IdsIn", out var idsEl)
                            ? idsEl.GetString()
                            : (je.TryGetProperty("ids_in", out var ids2) ? ids2.GetString() : null);
                        var limit = je.TryGetProperty("limit", out var lEl) && lEl.ValueKind == JsonValueKind.Number
                            ? lEl.GetInt32()
                            : (int?)null;

                        // normalize ids_in (these are product IDs from the vector step for our common flows)
                        var productIds = new List<int>();
                        if (!string.IsNullOrWhiteSpace(idsInVar)
                            && vars.TryGetValue(idsInVar!, out var v)
                            && v is List<int> list)
                        {
                            productIds = list;
                        }

                        // ---- productcategory path (unchanged, already working) ----
                        if (entity.Equals("productcategory", StringComparison.OrdinalIgnoreCase))
                        {
                            var rows = await _sql.GetProductCategoriesByProductIdsAsync(productIds, ct);

                            // Parse generic sort keys: [{ field, dir }]
                            var sortKeys = new List<(string field, bool desc)>();
                            if (je.TryGetProperty("sort", out var sortArr) && sortArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var s in sortArr.EnumerateArray())
                                {
                                    if (s.ValueKind != JsonValueKind.Object) continue;
                                    var field = s.TryGetProperty("field", out var fEl) ? fEl.GetString() ?? "" : "";
                                    var dir = s.TryGetProperty("dir", out var dEl) ? dEl.GetString() ?? "asc" : "asc";
                                    if (!string.IsNullOrWhiteSpace(field))
                                        sortKeys.Add((field.ToLowerInvariant(), string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase)));
                                }
                            }

                            static IOrderedEnumerable<Shared.DTOs.ProductCategoryDto> ApplyFirstKey(IEnumerable<Shared.DTOs.ProductCategoryDto> seq, (string field, bool desc) key) =>
                                key.field switch
                                {
                                    "price" => key.desc ? seq.OrderByDescending(x => x.Price) : seq.OrderBy(x => x.Price),
                                    "cost" => key.desc ? seq.OrderByDescending(x => x.Cost) : seq.OrderBy(x => x.Cost),
                                    "updatedstock" => key.desc ? seq.OrderByDescending(x => x.UpdatedStock) : seq.OrderBy(x => x.UpdatedStock),
                                    "productcategoryid" => key.desc ? seq.OrderByDescending(x => x.ProductCategoryId) : seq.OrderBy(x => x.ProductCategoryId),
                                    _ => key.desc ? seq.OrderByDescending(x => x.Price) : seq.OrderBy(x => x.Price),
                                };

                            static IOrderedEnumerable<Shared.DTOs.ProductCategoryDto> ApplyNextKey(IOrderedEnumerable<Shared.DTOs.ProductCategoryDto> seq, (string field, bool desc) key) =>
                                key.field switch
                                {
                                    "price" => key.desc ? seq.ThenByDescending(x => x.Price) : seq.ThenBy(x => x.Price),
                                    "cost" => key.desc ? seq.ThenByDescending(x => x.Cost) : seq.ThenBy(x => x.Cost),
                                    "updatedstock" => key.desc ? seq.ThenByDescending(x => x.UpdatedStock) : seq.ThenBy(x => x.UpdatedStock),
                                    "productcategoryid" => key.desc ? seq.ThenByDescending(x => x.ProductCategoryId) : seq.ThenBy(x => x.ProductCategoryId),
                                    _ => key.desc ? seq.ThenByDescending(x => x.Price) : seq.ThenBy(x => x.Price),
                                };

                            var best = rows
                                .GroupBy(pc => pc.ProductId)
                                .Select(g =>
                                {
                                    IOrderedEnumerable<Shared.DTOs.ProductCategoryDto> ordered;
                                    if (sortKeys.Count > 0)
                                    {
                                        ordered = ApplyFirstKey(g, sortKeys[0]);
                                        for (int i = 1; i < sortKeys.Count; i++)
                                            ordered = ApplyNextKey(ordered, sortKeys[i]);
                                    }
                                    else
                                    {
                                        // default: cheapest then smallest productCategoryId
                                        ordered = g.OrderBy(x => x.Price).ThenBy(x => x.ProductCategoryId);
                                    }
                                    return ordered.First();
                                })
                                .ToList();

                            // NEW: apply a global sort across the per-product winners
                            if (sortKeys.Count > 0)
                            {
                                var (field, desc) = sortKeys[0];
                                best = field switch
                                {
                                    "price" => desc ? best.OrderByDescending(x => x.Price).ToList()
                                                    : best.OrderBy(x => x.Price).ToList(),
                                    "cost" => desc ? best.OrderByDescending(x => x.Cost).ToList()
                                                    : best.OrderBy(x => x.Cost).ToList(),
                                    _ => desc ? best.OrderByDescending(x => x.Price).ToList()
                                                    : best.OrderBy(x => x.Price).ToList(),
                                };
                            }
                            else
                            {
                                best = best.OrderBy(x => x.Price).ToList(); // default
                            }

                            // take the limit AFTER global sort
                            if (limit is > 0) best = best.Take(limit.Value).ToList();


                            var pids = best.Select(b => b.ProductId).Distinct().ToList();
                            var products = await _sql.GetProductsByIdsAsync(pids, ct); // DTOs (has SupplierId, ProductDescription) :contentReference[oaicite:2]{index=2}

                            var joined =
                                (from b in best
                                 join p in products on b.ProductId equals p.ProductId
                                 select new ProductWithPriceDto
                                 {
                                     ProductId = p.ProductId,
                                     ProductName = p.ProductName,
                                     Description = p.ProductDescription,
                                     ImageUrl = p.ImageUrl,
                                     SupplierId = p.SupplierId,
                                     Price = b.Price,
                                     Cost = b.Cost,
                                     ProductCategoryId = b.ProductCategoryId
                                 }).ToList();

                            vars["last"] = joined;
                            break;
                        }

                        // ---- NEW: supplier path (generic, uses same ids_in=productIds) ----
                        if (entity.Equals("supplier", StringComparison.OrdinalIgnoreCase))
                        {
                            // 1) from product ids → get suppliers
                            var products = await _sql.GetProductsByIdsAsync(productIds, ct); // DTOs (SupplierId present)
                            var supplierIds = products.Select(p => p.SupplierId)
                                                      .Distinct()
                                                      .ToList();

                            if (supplierIds.Count == 0)
                            {
                                vars["last"] = Array.Empty<object>();
                                break;
                            }

                            // 2) fetch suppliers (you already have this helper; returns entities)
                            var suppliers = await _sql.GetSuppliersByIdsAsync(supplierIds); // Entities.Supplier 

                            // 3) generic sort support: [{ field: "name" | "supplierId", dir }]
                            var sortKeys = new List<(string field, bool desc)>();
                            if (je.TryGetProperty("sort", out var sortArr2) && sortArr2.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var s in sortArr2.EnumerateArray())
                                {
                                    if (s.ValueKind != JsonValueKind.Object) continue;
                                    var field = s.TryGetProperty("field", out var fEl) ? fEl.GetString() ?? "" : "";
                                    var dir = s.TryGetProperty("dir", out var dEl) ? dEl.GetString() ?? "asc" : "asc";
                                    if (!string.IsNullOrWhiteSpace(field))
                                        sortKeys.Add((field.ToLowerInvariant(), string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase)));
                                }
                            }

                            IOrderedEnumerable<Entities.Supplier>? ordered = null;
                            foreach (var (field, desc) in sortKeys)
                            {
                                if (ordered == null)
                                {
                                    ordered = field switch
                                    {
                                        "name" => desc ? suppliers.OrderByDescending(s => s.SupplierName) : suppliers.OrderBy(s => s.SupplierName),
                                        "supplierid" => desc ? suppliers.OrderByDescending(s => s.SupplierId) : suppliers.OrderBy(s => s.SupplierId),
                                        _ => desc ? suppliers.OrderByDescending(s => s.SupplierName) : suppliers.OrderBy(s => s.SupplierName),
                                    };
                                }
                                else
                                {
                                    ordered = field switch
                                    {
                                        "name" => desc ? ordered.ThenByDescending(s => s.SupplierName) : ordered.ThenBy(s => s.SupplierName),
                                        "supplierid" => desc ? ordered.ThenByDescending(s => s.SupplierId) : ordered.ThenBy(s => s.SupplierId),
                                        _ => desc ? ordered.ThenByDescending(s => s.SupplierName) : ordered.ThenBy(s => s.SupplierName),
                                    };
                                }
                            }
                            IEnumerable<Entities.Supplier> finalSeq = ordered ?? suppliers.OrderBy(s => s.SupplierName);
                            if (limit is > 0) finalSeq = finalSeq.Take(limit.Value);

                            // project a lightweight shape; swap to SupplierDto if you prefer
                            vars["last"] = finalSeq.Select(s => new {
                                s.SupplierId,
                                s.SupplierName,
                                s.Address,
                                s.PhoneNumber,
                                s.SupplierEmail
                            }).ToList();

                            break;
                        }

                        // Fallback: unsupported entity for now
                        vars["last"] = Array.Empty<object>();
                        break;
                    }


                case "join":
                    // not needed for v1 — we joined via service helpers
                    break;

                case "aggregate":
                    // optional later
                    break;
            }
        }

        return vars.TryGetValue("last", out var lastObj) ? lastObj! : Array.Empty<object>();
    }

}
