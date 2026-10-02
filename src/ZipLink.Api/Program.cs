using ZipLink.Core.Interfaces;
using ZipLink.Core.Services;
using ZipLink.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IShortUrlRepository, InMemoryShortUrlRepository>();
builder.Services.AddScoped<UrlShorteningService>();

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// The shortener's own page. Static files only - this service contains no AI and never
// will; the agent control panel is a separate, localhost-only application.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/urls", async (
    CreateShortUrlRequest request,
    UrlShorteningService service,
    HttpContext httpContext) =>
{
    try
    {
        var result = await service.CreateShortUrlAsync(request.Url);

        var baseUrl =
            $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";

        return Results.Created(
            $"/api/urls/{result.ShortCode}",
            new
            {
                result.OriginalUrl,
                result.ShortCode,
                shortUrl = $"{baseUrl}/{result.ShortCode}",
                result.CreatedAtUtc
            });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new
        {
            error = ex.Message
        });
    }
});

app.MapGet("/{code}", async (
    string code,
    UrlShorteningService service) =>
{
    var result = await service.ResolveAsync(code);

    if (result is null)
    {
        return Results.NotFound();
    }

    return Results.Redirect(result.OriginalUrl);
});

app.MapGet("/api/urls/{code}/analytics", async (
    string code,
    UrlShorteningService service) =>
{
    var result = await service.GetAsync(code);

    if (result is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        result.ShortCode,
        result.OriginalUrl,
        result.ClickCount,
        result.CreatedAtUtc
    });
});

app.Run();

record CreateShortUrlRequest(string Url);
