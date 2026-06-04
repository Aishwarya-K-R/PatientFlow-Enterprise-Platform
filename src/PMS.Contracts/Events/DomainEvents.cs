namespace PatientFlow.Contracts.Events;

/// <summary>
/// Payload for PatientCreated event.
/// </summary>
public class PatientCreatedEvent
{
    public int PatientId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Payload for PatientUpdated event.
/// </summary>
public class PatientUpdatedEvent
{
    public int PatientId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Payload for PatientDeleted event.
/// </summary>
public class PatientDeletedEvent
{
    public int PatientId { get; set; }
}

/// <summary>
/// Payload for BillingCreated event.
/// </summary>
public class BillingCreatedEvent
{
    public int PatientId { get; set; }
    public string AccountId { get; set; } = string.Empty;
}
