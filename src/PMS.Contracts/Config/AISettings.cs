namespace PatientFlow.Contracts.Config;

public class AISettings
{
    public string Model { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public List<string> Rules { get; set; } = new();
    public string NoDataMessage { get; set; } = "No data available";
    public string Endpoint { get; set; } = string.Empty;
}
