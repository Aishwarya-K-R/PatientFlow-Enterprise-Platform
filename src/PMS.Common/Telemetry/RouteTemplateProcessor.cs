using System.Diagnostics;
using OpenTelemetry;

namespace PatientFlow.Common.Telemetry;

/// <summary>
/// OTel processor that rewrites HTTP server span DisplayNames to use the
/// actual request path instead of the route template.
///
/// Why this exists:
///   ASP.NET Core's OTel instrumentation sets span names to "{METHOD} {route-template}".
///   For YARP gateways with catch-all routes (e.g. "/api/{**catch-all}"), every trace
///   shows "GET /api/{**catch-all}" — useless for distinguishing requests.
///
/// Why a processor and not EnrichWithHttpRequest:
///   Enrichment callbacks fire at request START. The framework overwrites DisplayName
///   at request END with its default "{method} {route-template}" pattern. A processor's
///   OnEnd runs LAST, so this rewrite is the final word.
///
/// Why both old and new conventions:
///   OpenTelemetry 1.7+ moved to stable semantic conventions:
///     url.path                ← was http.target
///     http.request.method     ← was http.method
///   We read the new ones first and fall back to legacy so this processor works
///   regardless of which convention the instrumentation emits.
/// </summary>
public class RouteTemplateProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        if (activity == null)
        {
            return;
        }

        // Only rewrite server (incoming HTTP request) spans. Client/internal/
        // producer/consumer spans have their own naming we shouldn't second-guess.
        if (activity.Kind != ActivityKind.Server)
        {
            base.OnEnd(activity);
            return;
        }

        var urlPath = activity.GetTagItem("url.path") as string
                      ?? activity.GetTagItem("http.target") as string;

        var method = activity.GetTagItem("http.request.method") as string
                     ?? activity.GetTagItem("http.method") as string;

        if (!string.IsNullOrEmpty(urlPath) && !string.IsNullOrEmpty(method))
        {
            activity.DisplayName = $"{method} {urlPath}";
        }

        base.OnEnd(activity);
    }
}
