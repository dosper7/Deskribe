using Deskribe.Sdk.Models;
using Deskribe.Sdk.Resources;

namespace Deskribe.Sdk;

public interface IMessagingProvider
{
    string ProviderType { get; }
    Task<ValidationResult> ValidateAsync(KafkaMessagingResource resource, ValidationContext ctx, CancellationToken ct = default);
    Task<ResourcePlanResult> PlanAsync(KafkaMessagingResource resource, PlanContext ctx, CancellationToken ct = default);
}
