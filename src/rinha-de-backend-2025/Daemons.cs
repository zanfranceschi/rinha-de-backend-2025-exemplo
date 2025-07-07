using System.Collections.Concurrent;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Npgsql;


public class Consumer
    : BackgroundService
{
    private enum TryPayMethod
    {
        WithAvailabilityCheck,
        WithDefaultFirst
    }

    private readonly record struct TryPaidResult(bool Success, string Service);
    private readonly ILogger<Consumer> logger;
    private readonly HttpClient httpClient;
    private readonly NpgsqlDataSource dataSource;
    private readonly ServicesAvailability servicesAvailability;
    private readonly ConfigPaymentProcessors configPaymentProcessors;
    private readonly ConcurrentQueue<ClientPaymentWireRequest> clientRequestsQueue;
    private Func<PaymentWireRequest, Task<TryPaidResult>> TryPay;

    public Consumer(
        ILogger<Consumer> logger,
        HttpClient httpClient,
        NpgsqlDataSource dataSource,
        ServicesAvailability servicesAvailability,
        ConfigPaymentProcessors configPaymentProcessors,
        ConcurrentQueue<ClientPaymentWireRequest> clientRequestsQueue)
    {
        this.logger = logger;
        this.httpClient = httpClient;
        this.dataSource = dataSource;
        this.servicesAvailability = servicesAvailability;
        this.configPaymentProcessors = configPaymentProcessors;
        this.clientRequestsQueue = clientRequestsQueue;

        var tryPayMethodConfig = Environment.GetEnvironmentVariable("TRY_PAYMENT_METHOD");

        if (Enum.TryParse(tryPayMethodConfig, out TryPayMethod tryPayMethod))
        {
            TryPay = tryPayMethod == TryPayMethod.WithAvailabilityCheck
                ? TryPayWithAvailabilityCheck : TryPayWithDefaultFirst;
        }
        else
        {
            TryPay = TryPayWithDefaultFirst;
        }

        logger.LogInformation($"Using '{tryPayMethodConfig ?? "TryPayWithDefaultFirst"}' as integration strategy.");
    }

    private async Task<TryPaidResult> RequestPayment(PaymentWireRequest request, string baseUrl, string service)
    {
        using StringContent json = new(JsonSerializer.Serialize(request, JsonSerializerOptions.Web),
                                        Encoding.UTF8,
                                        MediaTypeNames.Application.Json);
        try
        {
            using HttpResponseMessage response = await httpClient.PostAsync(
                $"{baseUrl}/payments",
                json);

            if (response.IsSuccessStatusCode)
            {
                return new TryPaidResult(true, service);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Erro ao processar pagamento via {service}");
        }

        return new TryPaidResult(false, service);
    }


    private async Task<TryPaidResult> TryPayWithAvailabilityCheck(PaymentWireRequest request)
    {
        if (servicesAvailability.UseDefault)
        {
            return await RequestPayment(request,
                configPaymentProcessors.Default.BaseUrl,
                configPaymentProcessors.Default.Name);
        }

        return await RequestPayment(request,
            configPaymentProcessors.Fallback.BaseUrl,
            configPaymentProcessors.Fallback.Name);
    }

    private async Task<TryPaidResult> TryPayWithDefaultFirst(PaymentWireRequest request)
    {
        var defaultResult = await RequestPayment(request,
            configPaymentProcessors.Default.BaseUrl,
            configPaymentProcessors.Default.Name);

        if (defaultResult.Success)
            return defaultResult;

        return await RequestPayment(request,
               configPaymentProcessors.Fallback.BaseUrl,
               configPaymentProcessors.Fallback.Name);
    }


    private async Task PersistPaymentLocally(PaymentWireRequest paymentWireRequest, string service)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand();
            cmd.CommandText = @"INSERT INTO payments (correlationId, amount, requested_at, service)
                                VALUES (@correlationId, @amount, @requested_at, @service);";
            cmd.Parameters.AddWithValue("correlationId", paymentWireRequest.CorrelationId);
            cmd.Parameters.AddWithValue("amount", paymentWireRequest.Amount);
            cmd.Parameters.AddWithValue("requested_at", paymentWireRequest.RequestedAt);
            cmd.Parameters.AddWithValue("service", service);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deu merda na persistência local do pagamento.");
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Consumer started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            while (clientRequestsQueue.TryDequeue(out ClientPaymentWireRequest clientPaymentWireRequest))
            {
                var paymentRequest = new PaymentWireRequest(
                    clientPaymentWireRequest.CorrelationId,
                    clientPaymentWireRequest.Amount,
                    DateTime.UtcNow);

                TryPaidResult result;
                do
                {
                    result = await TryPay(paymentRequest);
                }
                while (!result.Success);

                // se essa merda falhar, fode tudo – o estado dos sistemas fica inconsistente
                // foda-se, é rinha!
                await PersistPaymentLocally(paymentRequest, result.Service);
            }

            await Task.Delay(5, stoppingToken);
        }

        logger.LogInformation("Consumer ended.");
    }
}

public class PaymentProcessorSelection(
    ILogger<PaymentProcessorSelection> logger,
    HttpClient httpClient,
    ConfigPaymentProcessors configPaymentProcessors,
    ServicesAvailability servicesAvailability)
    : BackgroundService
{

    private bool UseDefault(bool successResponse, ServicesAvailabilityWireResponse availability)
    {
        return successResponse && !availability.Failing && availability.MinResponseTime <= 30;
    }

    private bool UseFallback(bool successResponse, ServicesAvailabilityWireResponse availability)
    {
        return successResponse && !availability.Failing;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using HttpResponseMessage responseDefault = await httpClient.GetAsync(
                $"{configPaymentProcessors.Default.BaseUrl}/payments/service-health");

            var availabilityDefault = await responseDefault.Content.ReadFromJsonAsync<ServicesAvailabilityWireResponse>(stoppingToken);

            if (UseDefault(responseDefault.IsSuccessStatusCode, availabilityDefault))
            {
                servicesAvailability.UseDefault = true;
                await Task.Delay(5000, stoppingToken);
                continue;
            }

            using HttpResponseMessage responseFallback = await httpClient.GetAsync(
                $"{configPaymentProcessors.Fallback.BaseUrl}/payments/service-health");

            var availabilityFallback = await responseFallback.Content.ReadFromJsonAsync<ServicesAvailabilityWireResponse>(stoppingToken);
            servicesAvailability.UseDefault = !UseFallback(responseFallback.IsSuccessStatusCode, availabilityFallback);
            await Task.Delay(5000, stoppingToken);
        }
    }
}
