using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ZibasheERP.API.Health;

public static class HealthCheckResponseWriter
{
    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var response = new
        {
            Status = report.Status.ToString(),
            DurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
            Checks = report.Entries.Select(entry => new
            {
                Name = entry.Key,
                Status = entry.Value.Status.ToString(),
                entry.Value.Description
            })
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(response));
    }
}
