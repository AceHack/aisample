using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.SemanticKernel;
using MimeDetective;

namespace MyOpenAIApp.Plugins
{
    public class ImageAnalysisPlugin
    {
        private readonly OpenAIClient _openAIClient;
        private static readonly HttpClient s_httpClient = new();

        // Constructor now only takes OpenAIClient (DI friendly)
        public ImageAnalysisPlugin(OpenAIClient openAIClient)
        {
            _openAIClient = openAIClient ?? throw new ArgumentNullException(nameof(openAIClient));
        }

        [KernelFunction("DescribeImageAsync"), Description("Analyzes an image from a URL and returns a description.")] // Explicitly name the function
        public async Task<string> DescribeImageAsync(
            [Description("The public URL of the image to analyze")] string imageUrl,
            [Description("The Azure OpenAI deployment name for the vision model")] string visionDeploymentName // Added parameter
            )
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return "No image URL provided.";
            }
            if (string.IsNullOrWhiteSpace(visionDeploymentName)) // Added validation for new parameter
            {
                return "No vision deployment name provided.";
            }

            byte[] imageBytes;
            string detectedMimeType = "application/octet-stream";

            try
            {
                imageBytes = await s_httpClient.GetByteArrayAsync(imageUrl);

                // --- Mime-Detective logic (remains the same) ---
                try
                {
                    using var memoryStream = new MemoryStream(imageBytes);
                    var inspector = new ContentInspectorBuilder()
                    {
                        Definitions = MimeDetective.Definitions.DefaultDefinitions.All()
                    }.Build();
                    var mimeTypeResult = inspector.Inspect(memoryStream).ByMimeType().FirstOrDefault();
                    if (mimeTypeResult != null && !string.IsNullOrEmpty(mimeTypeResult.MimeType))
                    {
                        detectedMimeType = mimeTypeResult.MimeType;
                        Console.WriteLine($"[Mime-Detective] Detected MIME type: {detectedMimeType}");
                    }
                    else
                    {
                        Console.WriteLine("[Mime-Detective] Could not detect MIME type. Attempting fallback.");
                        detectedMimeType = GetMediaTypeFromUrlFallback(imageUrl);
                        Console.WriteLine($"[URL Fallback] Detected MIME type: {detectedMimeType}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Mime-Detective] Error: {ex.Message}. Attempting fallback.");
                    detectedMimeType = GetMediaTypeFromUrlFallback(imageUrl);
                    Console.WriteLine($"[URL Fallback] Detected MIME type: {detectedMimeType}");
                }
                // --- End Mime-Detective logic ---

                // Use ChatCompletionsOptions with the deployment name from the parameter
                var chatCompletionsOptions = new ChatCompletionsOptions()
                {
                    DeploymentName = visionDeploymentName, // Use parameter here
                    Messages =
                    {
                        new ChatRequestUserMessage(
                            new ChatMessageTextContentItem("Describe this image in detail."),
                            new ChatMessageImageContentItem(BinaryData.FromBytes(imageBytes), detectedMimeType)
                        )
                    },
                    MaxTokens = 2000
                };

                // Call GetChatCompletionsAsync on the OpenAIClient
                Response<ChatCompletions> response = await _openAIClient.GetChatCompletionsAsync(chatCompletionsOptions);

                // Extract text content safely
                var responseChoice = response.Value?.Choices?.FirstOrDefault();
                var responseContent = responseChoice?.Message?.Content;

                return responseContent ?? "Could not get description from AI.";
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"Error downloading image {imageUrl}: {e.Message}");
                return $"Error downloading image: {e.Message}";
            }
            catch (RequestFailedException e)
            {
                Console.WriteLine($"Azure OpenAI request failed for image {imageUrl}: Status {e.Status}, Error: {e.Message}");
                return $"AI request failed: {e.Message}";
            }
            catch (Exception e)
            {
                Console.WriteLine($"An unexpected error occurred analyzing image {imageUrl}: {e.Message}");
                return $"Unexpected error analyzing image: {e.Message}";
            }
        }

        // Simple fallback based on URL extension (similar to previous logic)
        private static string GetMediaTypeFromUrlFallback(string url)
        {
            try
            {
                var uri = new Uri(url);
                string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                switch (extension)
                {
                    case ".png": return "image/png";
                    case ".jpg":
                    case ".jpeg": return "image/jpeg";
                    case ".gif": return "image/gif";
                    case ".webp": return "image/webp";
                    default: return "application/octet-stream";
                }
            }
            catch (UriFormatException ex)
            {
                Console.WriteLine($"Warning: Could not parse URI for fallback: {url}. Error: {ex.Message}");
                return "application/octet-stream";
            }
            catch (ArgumentNullException ex)
            {
                Console.WriteLine($"Warning: URL was null or empty for fallback. Error: {ex.Message}");
                return "application/octet-stream";
            }
        }
    }
}
