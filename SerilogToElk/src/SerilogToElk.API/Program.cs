using Elastic.Channels;
using Elastic.Ingest.Elasticsearch;
using Elastic.Ingest.Elasticsearch.DataStreams;
using Elastic.Serilog.Sinks;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var parsed = bool.TryParse(builder.Configuration["WithElasticSearchSink"], out var withElasticSearch);

if (parsed && withElasticSearch)
{
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .WriteTo.Async(a => a.Elasticsearch(
            [new Uri(builder.Configuration["Elasticsearch:ConnectionString"]!)],
            opts =>
            {
                opts.DataStream = new DataStreamName("logs", "console-example", "demo");
                opts.BootstrapMethod = BootstrapMethod.Failure;
                opts.ConfigureChannel = channelOpts =>
                {
                    channelOpts.BufferOptions = new BufferOptions
                    {
                        ExportMaxConcurrency = 10,
                    };
                };
            }, transport =>
            {
                // transport.Authentication(new BasicAuthentication(username, password)); // Basic Auth
                // transport.Authentication(new ApiKey(base64EncodedApiKey)); // ApiKey
            })).CreateLogger();

    builder.Host.UseSerilog();
}
else
{
    builder.Host.UseSerilog((context, configuration) =>
    {
        configuration.ReadFrom.Configuration(context.Configuration);
    });
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseSerilogRequestLogging(); // Logs HTTP requests

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
    {
        var forecast = Enumerable.Range(1, 5).Select(index =>
                new WeatherForecast
                (
                    DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                    Random.Shared.Next(-20, 55),
                    summaries[Random.Shared.Next(summaries.Length)]
                ))
            .ToArray();
        return forecast;
    })
    .WithName("GetWeatherForecast");

app.MapGet("/elasticSearchUrl", (IConfiguration configuration) => Results.Ok(new
{
    Url = configuration["Elasticsearch:ConnectionString"]
}));

app.MapGet("/work", () => Random.Shared.Next(0, 2) == 0 ? Results.InternalServerError() : Results.Ok());
app.MapPost("/work", () => Random.Shared.Next(0, 2) == 0 ? Results.InternalServerError() : Results.Ok());
app.MapPut("/work", () => Random.Shared.Next(0, 2) == 0 ? Results.InternalServerError() : Results.Ok());
app.MapDelete("/work", () => Random.Shared.Next(0, 2) == 0 ? Results.InternalServerError() : Results.Ok());

await app.RunAsync();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}