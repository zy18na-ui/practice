using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace dataAccess.Planning;

public sealed class PlanValidator
{
    private readonly Registry _reg;

    public PlanValidator(Registry reg) { _reg = reg; }

    public (bool ok, string? error, QueryPlan plan) Validate(QueryPlan plan)
    {
        return (true, null, plan);
    }
}
