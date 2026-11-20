namespace ReportBuilder.Core;

public sealed class LlmOptions
{
    /// <summary>"AzureOpenAI" (default), "Ollama" (also works for any OpenAI-compatible server such as vLLM or LM Studio).</summary>
    public string Provider { get; set; } = "AzureOpenAI";
    public string Endpoint { get; set; } = "";          // https://<resource>.openai.azure.com or the Foundry endpoint
    public string ApiKey { get; set; } = "";            // set via user-secrets, an environment variable or Key Vault. Leave empty in Azure to use the managed identity instead (needs the "Cognitive Services OpenAI User" role)
    public string ApiVersion { get; set; } = "2024-10-21";
    public double? Temperature { get; set; }            // leave null for reasoning models that only accept the default
    /// <summary>Deployment names by role: fast, planner, presenter, drafter.</summary>
    public Dictionary<string, string> Deployments { get; set; } = new();
}

public sealed class DatabaseOptions
{
    /// <summary>Login rb_reader: SELECT on the rpt schema only. Used for every report query.</summary>
    public string ReaderConnectionString { get; set; } = "";
    /// <summary>Login rb_app: read/write cat tables, insert on audit.ReportRun. Never used to run report SQL.</summary>
    public string AppConnectionString { get; set; } = "";
    /// <summary>Login rb_designer: SELECT on the ERP schemas listed in Designer:Schemas, CREATE VIEW and ALTER on the rpt schema. Used only by the View designer.</summary>
    public string DesignerConnectionString { get; set; } = "";
}

public sealed class DesignerOptions
{
    /// <summary>ERP schemas whose tables can be used as sources for a new view.</summary>
    public string[] Schemas { get; set; } = ["fin", "inv", "sal", "pur", "ar", "ap", "org", "hr", "mfg"];
    /// <summary>When true, a view must be approved by a catalog owner other than the one who designed it.</summary>
    public bool RequireSecondApprover { get; set; } = true;
}

public sealed class RolePolicy
{
    public string[] Companies { get; set; } = [];   // "*" means all
    public string[] Views { get; set; } = [];       // "*" means all rpt views
    public int? MaxRows { get; set; }
    public bool CatalogOwner { get; set; }
}

public sealed class PolicyOptions
{
    public string[] BlockedColumns { get; set; } = [];
    public string[] KnownCompanies { get; set; } = [];
    public bool AllowUnverified { get; set; }
    public int DefaultMaxRows { get; set; } = 5000;
    /// <summary>Keyed by role name. Windows mode: AD group (CONTOSO\\Finance-1000). EntraId mode: app role value (Finance-1000).</summary>
    public Dictionary<string, RolePolicy> Roles { get; set; } = new();
}

public sealed class UserContext
{
    public string UserName { get; init; } = "";
    /// <summary>Only the configured role names the user is a member of.</summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
}
