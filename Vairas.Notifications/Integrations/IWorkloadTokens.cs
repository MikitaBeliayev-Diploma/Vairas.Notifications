using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Vairas.Notifications.Business;
using Microsoft.Extensions.Options;
using Vairas.Notifications.Configuration.Options;

namespace Vairas.Notifications.Integrations;

public interface IWorkloadTokens
{
    Task<string> GetAsync(CancellationToken ct);
}
