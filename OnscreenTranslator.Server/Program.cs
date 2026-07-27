using System;
using System.IO;
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
        builder.Services.AddSingleton(AppSettings.Load());

        var app = builder.Build();

        // Health check
        app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

        // OCR endpoint
        app.MapPost("/api/ocr", async (HttpContext ctx, AppSettings settings, CancellationToken ct) =>
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                var root = doc.RootElement;

                var imageBase64 = root.GetProperty("imageBase64").GetString() ?? "";
                var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;

                // Save to temp file
                var tmp = Path.GetTempFileName() + ".png";
                try
                {
                    var bytes = Convert.FromBase64String(imageBase64);
                    await File.WriteAllBytesAsync(tmp, bytes, ct);

                    var provider = ProviderFactory.CreateOcrProvider(settings);
                    var text = await provider.RecognizeTextAsync(tmp, ct);
                    await ctx.Response.WriteAsJsonAsync(new { text }, cancellationToken: ct);
                }
                finally
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }, cancellationToken: ct);
            }
        });

        // Translation endpoint
        app.MapPost("/api/translate", async (HttpContext ctx, AppSettings settings, CancellationToken ct) =>
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                var root = doc.RootElement;

                var text = root.GetProperty("text").GetString() ?? "";
                var sourceLang = root.TryGetProperty("sourceLang", out var sl) ? sl.GetString() ?? "ja" : "ja";
                var targetLang = root.TryGetProperty("targetLang", out var tl) ? tl.GetString() ?? "en" : "en";

                var provider = ProviderFactory.CreateTranslationProvider(settings, settings.TranslationProvider);
                var translatedText = await provider.TranslateAsync(text, sourceLang, targetLang, ct);
                await ctx.Response.WriteAsJsonAsync(new { translatedText }, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }, cancellationToken: ct);
            }
        });

        app.Run();
    }
}
