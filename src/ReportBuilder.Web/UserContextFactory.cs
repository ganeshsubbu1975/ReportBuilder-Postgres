using System.Security.Claims;
using Microsoft.Extensions.Options;
using ReportBuilder.Core;

namespace ReportBuilder.Web;

/// <summary>Builds the pipeline's UserContext from the signed-in Windows/AD principal. Only configured role names are kept.</summary>
public sealed class UserContextFactory
{
    private readonly PolicyOptions _o;
    public UserContextFactory(IOptions<PolicyOptions> options) => _o = options.Value;

    public UserContext From(ClaimsPrincipal principal)
    {
        var name = principal.Identity?.Name ?? "unknown";
        var roles = _o.Roles.Keys.Where(r => principal.IsInRole(r)).ToArray();
        return new UserContext { UserName = name, Roles = roles };
    }
}
