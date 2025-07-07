using System.Collections.Concurrent;
using Npgsql;

var dbConnectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING")
    ?? "Host=localhost;Port=5432;Database=rinha;Username=postgres;Password=postgres";

var paymentProcessorServiceDefaultUrl = Environment.GetEnvironmentVariable("PAYMENT_PROCESSOR_SERVICE_DEFAULT_URL") ?? "http://localhost:8001";
var paymentProcessorServiceFallbacktUrl = Environment.GetEnvironmentVariable("PAYMENT_PROCESSOR_SERVICE_FALLBACK_URL") ?? "http://localhost:8002";

var clientRequestsQueue = new ConcurrentQueue<ClientPaymentWireRequest>();
var servicesAvailability = new ServicesAvailability();
var configPaymentProcessors = new ConfigPaymentProcessors(
    new ConfigPaymentProcessor("default", paymentProcessorServiceDefaultUrl),
    new ConfigPaymentProcessor("fallback", paymentProcessorServiceFallbacktUrl));

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(configPaymentProcessors);
builder.Services.AddSingleton(servicesAvailability);
builder.Services.AddSingleton(clientRequestsQueue);
builder.Services.AddHttpClient();
builder.Services.AddNpgsqlDataSource(dbConnectionString);
builder.Services.AddHostedService<Consumer>();
builder.Services.AddHostedService<PaymentProcessorSelection>();

var app = builder.Build();

app.MapPost("/payments", (
    ILogger<Endpoint> logger,
    ConcurrentQueue<ClientPaymentWireRequest> clientRequestsQueue,
    ClientPaymentWireRequest request) =>
{
    clientRequestsQueue.Enqueue(request);
    return Results.Accepted();
});

app.MapGet("/payments-summary", async (
    NpgsqlDataSource dataSource,
    DateTime? from,
    DateTime? to) =>
{
    await using var cmd = dataSource.CreateCommand();
    cmd.CommandText = @"WITH summary AS (
                            SELECT id, amount, service
                            FROM payments
                            WHERE requested_at BETWEEN @from AND @to
                               OR @from IS NULL
                               OR @to IS NULL
                        )
                        SELECT COUNT(id), COALESCE(SUM(amount), 0), service
                        FROM summary
                        GROUP by service;";
    cmd.Parameters.AddWithValue("@from", from.HasValue ? from.Value : DBNull.Value);
    cmd.Parameters.AddWithValue("@to", to.HasValue ? to.Value : DBNull.Value);

    var paymentSummary = new PaymentsSummary();

    using (var reader = await cmd.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync())
        {
            var totalRequests = reader.GetInt32(0);
            var totalAmount = reader.GetDecimal(1);
            var service = reader.GetString(2);
            paymentSummary.AddPaymentSummary(new PaymentSummary(totalRequests, totalAmount, service));
        }
    }

    return paymentSummary.ToPaymentsSummaryWireResponse();
});

app.MapPost("/purge-payments", async (
    NpgsqlDataSource dataSource) =>
{
    await using var cmd = dataSource.CreateCommand();
    cmd.CommandText = @"TRUNCATE TABLE payments RESTART IDENTITY;";
    await cmd.ExecuteNonQueryAsync();

    return Results.Ok(new { message = "All payments purged." });
});

app.MapGet("/", () => "up and running!");

app.Run();

public class Endpoint { }
