using System.Collections.Generic;
using System.Text.Json.Serialization;

// --- Input Models ---

public record Finding(string Description, string? ImageUrl = null);

public record StructuredImageAnalysisInput(string FormId, List<Finding> Findings);

// --- Output Models ---

public class Opportunity
{
    [JsonPropertyName("Title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("PotentialImpact")]
    public string PotentialImpact { get; set; } = string.Empty;

    // Add properties to store the source of the opportunity
    [JsonPropertyName("SourceDescription")]
    public string SourceDescription { get; set; } = string.Empty;

    [JsonPropertyName("SourceImageUrl")]
    public string? SourceImageUrl { get; set; } // Nullable if the source finding had no image
}

public class OpportunitiesReport
{
    [JsonPropertyName("IdentifiedOpportunities")]
    public List<Opportunity> IdentifiedOpportunities { get; set; } = new List<Opportunity>();
}
