using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OnscreenTranslator.Core.Providers;
using OnscreenTranslator.Core.Settings;

namespace OnscreenTranslator.Server;

/// <summary>
/// Standalone remote backend that wraps the core OCR/translation providers
/// behind a simple REST API. Run this on a machine with GPU access,
/// then point the desktop app's Remote Server settings at it.
///
/// Endpoints:
///   POST /api/ocr        { imageBase64, model? }  →  { text }
///   POST /api/translate   { text, sourceLang?, targetLang? }  →  { translatedText }
///   GET  /api/health     →  200
/// </summary>
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Limit request body to 10 MB to prevent OOM from large base64 payloads
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10 MB
        });

        builder.Services.AddSingleton(AppSettings.Load());

        var app = builder.Build();

        // ── Global exception handler middleware (catch-all for unhandled errors) ──
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OnscreenTranslator.Server] Unhandled exception: {ex}");
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsJsonAsync(new { error = "Internal server error" });
            }
        });

        // ── Authentication middleware ──
        app.Use(async (ctx, next) =>
        {
            // Bypass auth for the health endpoint
            if (ctx.Request.Path.StartsWithSegments("/api/health", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            var settings = ctx.RequestServices.GetRequiredService<AppSettings>();
            var expectedKey = settings.RemoteServer.ApiKey;

            // If no API key is configured, skip auth (open access)
            if (string.IsNullOrEmpty(expectedKey))
            {
                await next();
                return;
            }

            var authHeader = ctx.Request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsJsonAsync(new { error = "Missing or invalid Authorization header. Use: Bearer <api-key>" });
                return;
            }

            var token = authHeader["Bearer ".Length..].Trim();
            if (!string.Equals(token, expectedKey, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsJsonAsync(new { error = "Invalid API key" });
                return;
            }

            await next();
        });

        // ── Health check ──
        app.MapGet("/api/health", () =>
            Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

        // ── OCR endpoint ──
        app.MapPost("/api/ocr", async (HttpContext ctx, AppSettings settings, CancellationToken ct) =>
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                var root = doc.RootElement;

                // Validate required fields
                if (!root.TryGetProperty("imageBase64", out var imageBase64Elem)
                    || string.IsNullOrEmpty(imageBase64Elem.GetString()))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Required field 'imageBase64' is missing or empty" }, cancellationToken: ct);
                    return;
                }

                var imageBase64 = imageBase64Elem.GetString()!;

                // Save to a unique temp file (fix: no orphaned .tmp file)
                var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
                try
                {
                    var bytes = Convert.FromBase64String(imageBase64);
                    await File.WriteAllBytesAsync(tmp, bytes, ct);

                    var provider = ProviderFactory.CreateOcrProvider(settings);
                    try
                    {
                        var text = await provider.RecognizeTextAsync(tmp, ct);
                        await ctx.Response.WriteAsJsonAsync(new { text }, cancellationToken: ct);
                    }
                    finally
                    {
                        (provider as IDisposable)?.Dispose();
                    }
                }
                finally
                {
                    try { File.Delete(tmp); } catch { /* best effort */ }
                }
            }
            catch (FormatException) when (ctx.Response.StatusCode is 200 or 0)
            {
                // Invalid base64
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Invalid base64 encoding in 'imageBase64'" }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OnscreenTranslator.Server] /api/ocr error: {ex}");
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { error = "Internal server error" }, cancellationToken: ct);
            }
        });

        // ── Translation endpoint ──
        app.MapPost("/api/translate", async (HttpContext ctx, AppSettings settings, CancellationToken ct) =>
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                var root = doc.RootElement;

                // Validate required fields
                if (!root.TryGetProperty("text", out var textElem)
                    || string.IsNullOrEmpty(textElem.GetString()))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Required field 'text' is missing or empty" }, cancellationToken: ct);
                    return;
                }

                var text = textElem.GetString()!;
                var sourceLang = root.TryGetProperty("sourceLang", out var sl) ? sl.GetString() ?? "ja" : "ja";
                var targetLang = root.TryGetProperty("targetLang", out var tl) ? tl.GetString() ?? "en" : "en";

                var provider = ProviderFactory.CreateTranslationProvider(settings, settings.TranslationProvider);
                try
                {
                    var translatedText = await provider.TranslateAsync(text, sourceLang, targetLang, ct);
                    await ctx.Response.WriteAsJsonAsync(new { translatedText }, cancellationToken: ct);
                }
                finally
                {
                    (provider as IDisposable)?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OnscreenTranslator.Server] /api/translate error: {ex}");
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { error = "Internal server error" }, cancellationToken: ct);
            }
        });

        app.Run();
    }
}
