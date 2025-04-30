using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.DependencyInjection; // Keep for AddAzureOpenAIChatCompletion
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using MyOpenAIApp.Plugins;

// Main entry point needs to be async
public class Program
{
    public static async Task Main(string[] args)
    {
        // Read configuration from environment variables
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var endpointString = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME"); // Used for both text and vision

        // --- Environment Variable Validation --- (Adjusted)
        if (string.IsNullOrEmpty(apiKey)) { Console.WriteLine("Error: AZURE_OPENAI_API_KEY environment variable not set."); return; }
        if (string.IsNullOrEmpty(endpointString)) { Console.WriteLine("Error: AZURE_OPENAI_ENDPOINT environment variable not set."); return; }
        if (string.IsNullOrEmpty(deploymentName)) { Console.WriteLine("Error: AZURE_OPENAI_DEPLOYMENT_NAME environment variable not set."); return; }
        if (!Uri.TryCreate(endpointString, UriKind.Absolute, out var endpoint)) { Console.WriteLine($"Error: Invalid URI format for AZURE_OPENAI_ENDPOINT: {endpointString}"); return; }


        // --- Semantic Kernel Setup ---
        var kernelBuilder = Kernel.CreateBuilder();

        // Configure services - only add the chat completion service here
        kernelBuilder.Services.AddAzureOpenAIChatCompletion(
            deploymentName, // Text model deployment
            endpointString,
            apiKey);

        // Build the Kernel
        var kernel = kernelBuilder.Build();

        // --- Setup for Image Analysis Plugin (Manual Instantiation) ---
        // Create the OpenAIClient needed by the plugin manually
        var visionClient = new OpenAIClient(endpoint, new AzureKeyCredential(apiKey));

        // Instantiate the plugin manually, passing only the client
        var imagePlugin = new ImageAnalysisPlugin(visionClient);

        // Import the manually created plugin instance into the kernel using a simple name
        kernel.ImportPluginFromObject(imagePlugin, "ImageAnalysis"); // Use "ImageAnalysis" as the plugin name
        // --- End Plugin Setup ---

        // --- Define the Analysis Prompt (Semantic Function) ---
        // Incorporate persona from agents.yaml
        var persona = """
        You are an expert Business Opportunity Analyst skilled at interpreting visual information and connecting it
        to market trends and business potential. You use an advanced image analysis tool
        to understand the content and context of images provided. Your goal is to analyze images and associated text
        to identify potential business opportunities, trends, or insights.
        """;

        var analysisTask = """
        Analyze the following findings from a form (Form ID: {{$formId}}) to identify potential business opportunities or consulting services.
        Consider the descriptions and any associated image analysis provided. Focus on emerging trends or complex areas revealed by the analyses.

        Findings:
        {{$findingsJson}}

        Based *only* on the information provided in the findings, generate a report outlining potential business opportunities.
        Crucially, you MUST generate at least one distinct opportunity derived from the analysis of EACH finding provided. Ensure that the final list reflects insights from all analyzed findings.

        For each opportunity, provide:
        1. A title.
        2. A brief description.
        3. Its potential impact (High/Medium/Low).
        4. The 'SourceDescription' from the specific finding that primarily inspired the opportunity.
        5. The 'SourceImageUrl' from the specific finding (if one was provided for that finding).

        Format the output as a JSON object matching the following structure:
        {
          "IdentifiedOpportunities": [
            {
              "Title": "Opportunity Title",
              "Description": "Detailed description of the opportunity based on findings.",
              "PotentialImpact": "High/Medium/Low impact assessment.",
              "SourceDescription": "The original description text from the finding.",
              "SourceImageUrl": "The URL of the image from the finding, or null if none."
            }
            // ... more opportunities
          ]
        }

        Ensure the output is valid JSON. If no opportunities are identified, return an empty list: { "IdentifiedOpportunities": [] }
        """;

        // Combine persona and task for the final prompt
        var analysisPrompt = $"{persona}\n\n{analysisTask}";


        // Create the Semantic Function from the prompt
#pragma warning disable SKEXP0010 // Disable warning for experimental ResponseFormat
        var analysisFunction = kernel.CreateFunctionFromPrompt(
            analysisPrompt,
            executionSettings: new OpenAIPromptExecutionSettings { ResponseFormat = "json_object" } // Request JSON output
        );
#pragma warning restore SKEXP0010 // Restore warning


        // --- Prepare Input Data ---
        var sampleInput = new StructuredImageAnalysisInput(
            FormId: "FORM_CSHARP_SEMANTIC_KERNEL_TEST",
            Findings: new List<Finding>
            {
                new Finding("Finding 1: Overview diagram.", "https://miro.medium.com/v2/resize:fit:4800/format:webp/1*vdvf-ds54uMEZQO7ZpY9iA.png"),
                new Finding("Finding 2: Cloud architecture.", "https://www.networkbachelor.com/wp-content/uploads/2021/01/Azure.png"),
                new Finding("Finding 3: Text-only observation about market trends.") // No image
            }
        );


        // --- Orchestration Logic ---
        Console.WriteLine($"Starting analysis for Form ID: {sampleInput.FormId}");
        var detailedFindings = new List<object>();
        var imageAnalysisTasks = new List<Task<(Finding finding, string analysisResult)>>(); // Store tasks and findings

        foreach (var finding in sampleInput.Findings)
        {
            if (!string.IsNullOrWhiteSpace(finding.ImageUrl))
            {
                // Launch the analysis task but don't await it here
                imageAnalysisTasks.Add(AnalyzeImageAsync(kernel, finding, deploymentName));
            }
            else
            {
                // For findings without images, add them directly or handle later
                // Option: Add directly with "N/A" analysis
                detailedFindings.Add(new { finding.Description, finding.ImageUrl, ImageAnalysis = "N/A" });
            }
        }

        // Wait for all image analysis tasks to complete concurrently
        var analysisResults = await Task.WhenAll(imageAnalysisTasks);

        // Process the results of the completed tasks
        foreach (var result in analysisResults)
        {
            detailedFindings.Add(new { result.finding.Description, result.finding.ImageUrl, ImageAnalysis = result.analysisResult });
        }

        // Sort detailedFindings to maintain original order if necessary (optional)
        // detailedFindings = detailedFindings.OrderBy(f => sampleInput.Findings.IndexOf( /* logic to find original finding */ )).ToList();


        var findingsJson = JsonSerializer.Serialize(detailedFindings, new JsonSerializerOptions { WriteIndented = true });
        var kernelArguments = new KernelArguments
        {
            { "formId", sampleInput.FormId },
            { "findingsJson", findingsJson }
        };

        Console.WriteLine("\nInvoking main analysis function...");
        try
        {
            var analysisResult = await kernel.InvokeAsync(analysisFunction, kernelArguments);
            var resultJson = analysisResult.GetValue<string>();

            Console.WriteLine("\n--- Analysis Result (Raw JSON) ---");
            Console.WriteLine(resultJson);

            if (!string.IsNullOrWhiteSpace(resultJson))
            {
                try
                {
                    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var report = JsonSerializer.Deserialize<OpportunitiesReport>(resultJson, jsonOptions);

                    if (report != null && report.IdentifiedOpportunities != null && report.IdentifiedOpportunities.Any())
                    {
                        Console.WriteLine("\n--- Identified Opportunities ---");
                        foreach (var opp in report.IdentifiedOpportunities)
                        {
                            Console.WriteLine($"  Title: {opp.Title}");
                            Console.WriteLine($"  Description: {opp.Description}");
                            Console.WriteLine($"  Impact: {opp.PotentialImpact}");
                            Console.WriteLine($"  Source Finding: {opp.SourceDescription}"); // Display source
                            if (!string.IsNullOrEmpty(opp.SourceImageUrl))
                            {
                                Console.WriteLine($"  Source Image: {opp.SourceImageUrl}"); // Display source image if present
                            }
                            Console.WriteLine(); // Add blank line
                        }
                    }
                    else
                    {
                        Console.WriteLine("\nNo business opportunities identified in the report.");
                    }
                }
                catch (JsonException jsonEx)
                {
                    Console.WriteLine($"\nError deserializing analysis result: {jsonEx.Message}");
                    Console.WriteLine("Please check the raw JSON output above for potential formatting issues.");
                }
            }
            else
            {
                Console.WriteLine("\nAnalysis function returned empty or null result.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nAn error occurred during kernel invocation: {ex.Message}");
            // Log the full exception for debugging
            Console.WriteLine(ex.ToString());
        }

        Console.WriteLine("\nExecution finished.");
    } // End Main

    // Helper function to encapsulate image analysis logic for parallel execution
    private static async Task<(Finding finding, string analysisResult)> AnalyzeImageAsync(Kernel kernel, Finding finding, string deploymentName)
    {
        Console.WriteLine($"Analyzing image for: {finding.Description}");
        string imageDescription;
        try
        {
            var functionToInvoke = kernel.Plugins["ImageAnalysis"]["DescribeImageAsync"];
            var arguments = new KernelArguments()
            {
                { "imageUrl", finding.ImageUrl },
                { "visionDeploymentName", deploymentName }
            };

            var imageResult = await functionToInvoke.InvokeAsync(kernel, arguments);
            imageDescription = imageResult.GetValue<string>() ?? "Analysis failed or returned null.";
            Console.WriteLine($" -> Image Analysis Result for '{finding.Description}': {imageDescription.Substring(0, Math.Min(imageDescription.Length, 100))}...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($" -> Error analyzing image {finding.ImageUrl}: {ex.Message}");
            // Consider logging the full exception ex.ToString()
            imageDescription = $"Error analyzing image: {ex.GetType().Name}";
        }
        return (finding, imageDescription);
    }

} // End Program
