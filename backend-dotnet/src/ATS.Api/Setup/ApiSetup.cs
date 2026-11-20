using System.Reflection;
using Ats.Api.Auth;
using Ats.Api.Middleware;
using Ats.Application.Configuration;
using Ats.Application.Json;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Serilog;

namespace Ats.Api.Setup;

internal static class ApiSetup
{
    private const string ApiTitle = "Office Attendance Tracker API";

    private const string ApiDescription =
        "Attendance tracking with employee, manager and organisation dashboards. Authenticate via POST " +
        "/api/auth/login (HttpOnly session cookie); send the CSRF token in the X-CSRF-Token header for " +
        "state-changing requests.";

    public static IServiceCollection AddAtsApi(this IServiceCollection services)
    {
        services.AddControllers(o => o.Filters.Add<ValidationFilter>())
            .AddJsonOptions(o => AtsJson.Configure(o.JsonSerializerOptions))
            .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ValidationErrors.Respond);
        services.ConfigureHttpJsonOptions(o => AtsJson.Configure(o.SerializerOptions));
        services.AddSessionAuth();

        // CORS as configured in Python: credentials, the four write methods plus GET, the CSRF / request-id headers.
        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<AtsSettings>((options, settings) =>
            options.AddDefaultPolicy(policy =>
            {
                var origins = settings.CorsOriginList;
                if (origins.Contains("*"))
                {
                    policy.SetIsOriginAllowed(_ => true);
                }
                else
                {
                    policy.WithOrigins([.. origins]);
                }

                policy.AllowCredentials()
                    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
                    .WithHeaders("Content-Type", settings.CsrfHeaderName, "X-Request-ID")
                    .WithExposedHeaders("Content-Disposition", "X-Request-ID")
                    .SetPreflightMaxAge(TimeSpan.FromSeconds(600)); // Starlette's default max_age
            }));

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(o =>
        {
            o.SwaggerDoc("openapi", new OpenApiInfo { Title = ApiTitle, Version = "1.0.0", Description = ApiDescription });
            var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
            if (File.Exists(xml))
            {
                o.IncludeXmlComments(xml);
            }
        });
        return services;
    }

    /// <summary>Middleware order: request context (outermost, so every response gets the headers), CORS, errors.</summary>
    public static WebApplication UseAtsPipeline(this WebApplication app)
    {
        app.UseMiddleware<RequestContextMiddleware>();
        app.UseSerilogRequestLogging();
        app.UseRouting();
        app.UseCors();
        app.UseMiddleware<ErrorHandlingMiddleware>();
        app.UseStatusCodePages(async context =>
        {
            // Unknown route / wrong method with an empty body: Starlette's HTTPException handler output.
            var status = context.HttpContext.Response.StatusCode;
            var (code, message) = status switch
            {
                404 => ("not_found", "Not Found"),
                405 => ("method_not_allowed", "Method Not Allowed"),
                _ => ("http_error", ReasonPhrase(status)),
            };
            await ErrorEnvelope.WriteAsync(context.HttpContext, status, code, message).ConfigureAwait(false);
        });

        // Docs where FastAPI serves them: /api/openapi.json, /api/docs (Swagger UI), /api/redoc.
        app.UseSwagger(o => o.RouteTemplate = "api/{documentName}.json");
        app.UseSwaggerUI(o =>
        {
            o.RoutePrefix = "api/docs";
            o.SwaggerEndpoint("/api/openapi.json", ApiTitle);
            o.DocumentTitle = $"{ApiTitle} - Swagger UI";
        });
        app.MapGet("/api/redoc", () => Results.Content(RedocPage, "text/html; charset=utf-8")).ExcludeFromDescription();

        // Inside the error middleware: a refused session / CSRF / role is raised and rendered as the envelope.
        app.UseAuthorization();
        app.MapControllers();
        return app;
    }

    private static string ReasonPhrase(int status) =>
        Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } phrase
            ? phrase
            : "Error";

    /// <summary>The page FastAPI serves at redoc_url (ReDoc from its CDN, pointed at our OpenAPI document).</summary>
    private const string RedocPage = """
        <!DOCTYPE html>
        <html>
        <head>
        <title>Office Attendance Tracker API - ReDoc</title>
        <meta charset="utf-8"/>
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>body { margin: 0; padding: 0; }</style>
        </head>
        <body>
        <noscript>ReDoc requires Javascript to function. Please enable it to browse the documentation.</noscript>
        <redoc spec-url="/api/openapi.json"></redoc>
        <script src="https://cdn.jsdelivr.net/npm/redoc@2/bundles/redoc.standalone.js"></script>
        </body>
        </html>
        """;
}
