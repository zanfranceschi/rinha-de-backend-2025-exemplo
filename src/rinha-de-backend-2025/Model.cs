using System.Linq;


public readonly record struct ClientPaymentWireRequest(Guid CorrelationId, decimal Amount);

public readonly record struct PaymentWireRequest(Guid CorrelationId, decimal Amount, DateTime RequestedAt);

public readonly record struct PaymentRequest(Guid CorrelationId, decimal Amount, string Service);

public readonly record struct ServicesAvailabilityWireResponse(bool Failing, int MinResponseTime);

public record class PaymentServicesConfiguration(string DefaultName, string DefaultBaseUrl, string FallbackName, string FallbackBaseUrl);

public class ServicesAvailability
{
    public bool UseDefault { get; set; } = true;
}

public readonly record struct ConfigPaymentProcessor(string Name, string BaseUrl);
public record class ConfigPaymentProcessors(ConfigPaymentProcessor Default, ConfigPaymentProcessor Fallback);

public readonly record struct PaymentSummary(int TotalRequests, decimal TotalAmount, string Service);
public readonly record struct PaymentSummaryWire(int TotalRequests, decimal TotalAmount);

public readonly record struct PaymentsSummaryWireResponse(PaymentSummaryWire? Default, PaymentSummaryWire? Fallback);

public class PaymentsSummary
{
    private IList<PaymentSummary> _paymentSummaries = [];

    public void AddPaymentSummary(PaymentSummary paymentSummary)
    {
        _paymentSummaries.Add(paymentSummary);
    }


    public PaymentsSummaryWireResponse ToPaymentsSummaryWireResponse()
    {
        var paymentSummaryDefault = _paymentSummaries.Where(p => p.Service == "default").FirstOrDefault();
        var paymentSummaryFallback = _paymentSummaries.Where(p => p.Service == "fallback").FirstOrDefault();
        return new PaymentsSummaryWireResponse(
            new PaymentSummaryWire(paymentSummaryDefault.TotalRequests, paymentSummaryDefault.TotalAmount),
            new PaymentSummaryWire(paymentSummaryFallback.TotalRequests, paymentSummaryFallback.TotalAmount));
    }
}
