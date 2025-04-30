using System;
using System.Collections.Generic;
using System.IO; // Added for Path.GetExtension and MemoryStream
using System.Linq; // Added for LINQ methods
using System.Net.Http;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using OpenAI.Chat; // Added this using directive
using MimeDetective; // Use the correct namespace for Mime-Detective

// Declare a static HttpClient instance accessible to local functions
// This avoids socket exhaustion issues by reusing the same client.
HttpClient sharedHttpClient = new(); // Use a local variable accessible by local functions

// Helper function to determine media type from URL (now a static local function)
static string GetMediaTypeFromUrl(string url)
{
    try
    {
        var uri = new Uri(url);
        string? formatParam = null; // Use nullable reference type

        // Manually parse the query string
        if (!string.IsNullOrEmpty(uri.Query))
        {
            // Remove the leading '?'
            string query = uri.Query.Substring(1);
            var parts = query.Split('&');
            foreach (var part in parts)
            {
                var keyValue = part.Split('=');
                if (keyValue.Length == 2 && keyValue[0].Equals("format", StringComparison.OrdinalIgnoreCase))
                {
                    formatParam = keyValue[1];
                    break; // Found the format parameter
                }
            }
        }

        // Check query parameters first (like format=webp)
        if (!string.IsNullOrEmpty(formatParam))
        {
            return $"image/{formatParam.ToLowerInvariant()}"; // Use ToLowerInvariant
        }

        // Fallback to file extension
        string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        switch (extension)
        {
            case ".png": return "image/png";
            case ".jpg":
            case ".jpeg": return "image/jpeg";
            case ".gif": return "image/gif";
            case ".webp": return "image/webp";
            // Add more cases as needed
            default: return "application/octet-stream"; // Default or throw an error
        }
    }
    catch (UriFormatException)
    {
        // Handle invalid URL format if necessary
        Console.WriteLine($"Warning: Could not parse URI: {url}");
        return "application/octet-stream";
    }
}


// Read configuration from environment variables
var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
var endpointString = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME");
var apiVersionString = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION"); // Read API Version

if (string.IsNullOrEmpty(apiKey))
{
    Console.WriteLine("Error: AZURE_OPENAI_API_KEY environment variable not set.");
    return; // Exit if the key is not found
}
if (string.IsNullOrEmpty(endpointString))
{
    Console.WriteLine("Error: AZURE_OPENAI_ENDPOINT environment variable not set.");
    return; // Exit if the endpoint is not found
}
if (string.IsNullOrEmpty(deploymentName))
{
    Console.WriteLine("Error: AZURE_OPENAI_DEPLOYMENT_NAME environment variable not set.");
    return; // Exit if the deployment name is not found
}
// Note: AZURE_OPENAI_MODEL_NAME is typically implicitly handled by the deployment name in Azure.

if (!Uri.TryCreate(endpointString, UriKind.Absolute, out var endpoint))
{
    Console.WriteLine($"Error: Invalid URI format for AZURE_OPENAI_ENDPOINT: {endpointString}");
    return; // Exit if the URI is invalid
}

// Configure client options
// Note: Explicitly setting the API version via AzureOpenAIClientOptions.Version
// is not supported in Azure.AI.OpenAI v2.1.0. The SDK typically uses a
// default version or determines it based on the endpoint.
var clientOptions = new AzureOpenAIClientOptions();

// Use the configuration from environment variables
AzureOpenAIClient azureClient = new(
    endpoint,
    new AzureKeyCredential(apiKey),
    clientOptions); // Pass the configured options
ChatClient chatClient = azureClient.GetChatClient(deploymentName);

// Define the image URL (could also be moved to config)
string imageUrl = "https://miro.medium.com/v2/resize:fit:4800/format:webp/1*vdvf-ds54uMEZQO7ZpY9iA.png";

// Prepare message content parts asynchronously using the shared HttpClient
async Task<List<ChatMessageContentPart>> CreateContentPartsAsync(string urlForImage)
{
    byte[] imageBytes;

    // Download the image using the shared HttpClient instance
    try
    {
        imageBytes = await sharedHttpClient.GetByteArrayAsync(urlForImage);
    }
    catch (HttpRequestException e)
    {
        Console.WriteLine($"Error downloading image: {e.Message}");
        return new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart("Please describe the image (failed to load)") };
    }

    // Detect MIME type from content using Mime-Detective
    string detectedMimeType = "application/octet-stream"; // Default fallback
    try
    {
        // Mime-Detective works with streams, so wrap the byte array
        using (var memoryStream = new MemoryStream(imageBytes))
        {
            var inspector = new ContentInspectorBuilder()
            {
                Definitions = MimeDetective.Definitions.DefaultDefinitions.All()
            }.Build();
            var mimeTypeResult = inspector.Inspect(memoryStream).ByMimeType().Single();
            if (mimeTypeResult != null && !string.IsNullOrEmpty(mimeTypeResult.MimeType))
            {
                detectedMimeType = mimeTypeResult.MimeType;
                Console.WriteLine($"[Mime-Detective] Detected MIME type: {detectedMimeType}");
            }
            else
            {
                Console.WriteLine("[Mime-Detective] Could not detect MIME type from content. Falling back to URL-based detection.");
                detectedMimeType = GetMediaTypeFromUrl(urlForImage);
                Console.WriteLine($"[URL Fallback] Detected MIME type: {detectedMimeType}");
            }
        }
    }
    catch (Exception ex) // Catch potential errors during detection
    {
        Console.WriteLine($"[Mime-Detective] Error during detection: {ex.Message}. Falling back to URL-based detection.");
        detectedMimeType = GetMediaTypeFromUrl(urlForImage);
        Console.WriteLine($"[URL Fallback] Detected MIME type: {detectedMimeType}");
    }

    // Return list with text and the successfully loaded image part
    return new List<ChatMessageContentPart>
    {
        ChatMessageContentPart.CreateTextPart("Please describe the image"),
        ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(imageBytes), detectedMimeType, ChatImageDetailLevel.Auto)
    };
}


List<ChatMessage> messages = new List<ChatMessage>()
{
    new SystemChatMessage("You are a helpful assistant."),
    // Create UserChatMessage content asynchronously
    // Note: This requires adjusting how messages are constructed or passed to the client
    // For simplicity here, we'll await the creation before initializing the list.
    // A more complex scenario might involve building the message list differently.
};

// Await the async content creation before adding the UserChatMessage
// Pass the specific imageUrl to the helper function
var userMessageContentParts = await CreateContentPartsAsync(imageUrl);
messages.Add(new UserChatMessage(userMessageContentParts));


try
{
    // Call CompleteChatAsync since we are in an async context
    var response = await chatClient.CompleteChatAsync(messages);
    // Accessing the content correctly based on typical Azure SDK patterns
    if (response.Value.Content.Count > 0 && response.Value.Content[0].Kind == ChatMessageContentPartKind.Text)
    {
        Console.WriteLine(response.Value.Content[0].Text);
    }
    else
    {
        Console.WriteLine("No text response content received.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"An error occurred: {ex.Message}");
}

// Ensure the HttpClient is disposed when the application exits (optional for console apps, but good practice)
sharedHttpClient.Dispose();
