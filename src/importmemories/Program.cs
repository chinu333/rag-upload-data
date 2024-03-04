using BlingFire;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Memory.Qdrant;
using Microsoft.SemanticKernel.Connectors.Memory.AzureCognitiveSearch;

internal class Program
{
    /// <summary>
    /// This program imports text files into a Qdrant VectorDB using Semantic Kernel.
    /// </summary>
    /// <param name="memoryType">Either "qdrant" or "azurecognitivesearch"</param>
    /// <param name="memoryUrl">The URL to a running Qdrant VectorDB (e.g., http://localhost:6333) or to your Azure Cognitive Search endpoint.</param>
    /// <param name="collection">Name of the database collection in which to import (e.g., "mycollection").</param>
    /// <param name="textFile">Text files to import.</param>
    static async Task Main(string memoryType, string memoryUrl, string collection, params FileInfo[] textFile)
    {
        // Validate arguments.
        if (textFile.Length == 0)
        {
            Console.Error.WriteLine("No text files provided. Use '--help' for usage.");
            return;
        }

        IKernel kernel;
        IConfiguration config = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        if (memoryType.Equals("qdrant", StringComparison.InvariantCultureIgnoreCase))
        {
            // Get the OpenAI API key from the configuration.
            string? openAiApiKey = ""; //config["AZURE OPENAI_APIKEY"];

            if (string.IsNullOrWhiteSpace(openAiApiKey))
            {
                Console.Error.WriteLine("Please set the 'OPENAI_APIKEY' user secret with your OpenAI API key.");
                return;
            }

            // Create a new memory store that will store the embeddings in Qdrant.
            Uri qdrantUri = new Uri(memoryUrl);
            QdrantMemoryStore memoryStore = new QdrantMemoryStore(
                host: $"{qdrantUri.Scheme}://{qdrantUri.Host}",
                port: qdrantUri.Port,
                vectorSize: 1536);

            // Create a new kernel with an OpenAI Embedding Generation service.
            kernel = new KernelBuilder()
                .Configure(c => c.AddAzureTextEmbeddingGenerationService(
                    deploymentName: "text-embedding-ada-002",
                    endpoint: "ENDPOINT",
                    apiKey: openAiApiKey))
                .WithMemoryStorage(memoryStore)
                .Build();

        }
        else if (memoryType.Equals("azurecognitivesearch", StringComparison.InvariantCultureIgnoreCase))
        {
            // Get the Azure Cognitive Search API key from the environment.
            // string? azureCognitiveSearchApiKey = config["AZURE_COGNITIVE_SEARCH_APIKEY"];
            string? azureCognitiveSearchApiKey ="AZURE_COGNITIVE_SEARCH_APIKEY";

            if (string.IsNullOrWhiteSpace(azureCognitiveSearchApiKey))
            {
                Console.Error.WriteLine("Please set the 'AZURE_COGNITIVE_SEARCH_APIKEY' user secret with your Azure Cognitive Search API key.");
                return;
            }

            AzureCognitiveSearchMemory memory = new AzureCognitiveSearchMemory(
                memoryUrl,
                azureCognitiveSearchApiKey
            );

            // Create a new kernel with an OpenAI Embedding Generation service.
            kernel = new KernelBuilder()
                .WithMemory(memory)
                .Build();
        }
        else
        {
            Console.Error.WriteLine("Not a supported memory type. Use '--help' for usage.");
            return;
        }

        await ImportMemoriesAsync(kernel, collection, textFile);
    }

    static async Task ImportMemoriesAsync(IKernel kernel, string collection, params FileInfo[] textFile)
    {

        // Use sequential memory IDs; this makes it easier to retrieve sentences near a given sentence.
        int memoryId = 0;

        // Import the text files.
        int fileCount = 0;
        foreach (FileInfo fileInfo in textFile)
        {
            Console.WriteLine($"Importing [{++fileCount}/{fileInfo.Length}] {fileInfo.FullName}");

            if(fileInfo.Extension == ".txt")
            {
                Console.WriteLine("Importing text file.");
                // Read the text file.
                string text = File.ReadAllText(fileInfo.FullName);

                // Split the text into sentences.
                string[] sentences = BlingFireUtils.GetSentences(text).ToArray();

                // Save each sentence to the memory store.
                int sentenceCount = 0;
                foreach (string sentence in sentences)
                {
                    ++sentenceCount;
                    if (sentenceCount % 10 == 0)
                    {
                        // Log progress every 10 sentences.
                        Console.WriteLine($"[{fileCount}/{fileInfo.Length}] {fileInfo.FullName}: {sentenceCount}/{sentences.Length}");
                    }

                    await kernel.Memory.SaveInformationAsync(
                        collection: collection,
                        text: sentence,
                        id: memoryId++.ToString(),
                        description: sentence);
                }
            }
            else if(fileInfo.Extension == ".pdf")
            {
                await ImportFromPDFAsync(kernel, collection, fileInfo, memoryId);
            }
            else if(fileInfo.Extension == ".csv")
            {
                await ImportFromCSVAsync(kernel, collection, fileInfo, memoryId);
            }
        }

        Console.WriteLine("Done!");
    }

    static async Task ImportFromPDFAsync(IKernel kernel, string collection, FileInfo fileInfo, int memoryId)
    {
        Console.WriteLine("Importing pdf file.");

        //set `<your-endpoint>` and `<your-key>` variables with the values from the Azure portal to create your `AzureKeyCredential` and `DocumentIntelligenceClient` instance
        string endpoint = "";
        string key = "";
        AzureKeyCredential credential = new AzureKeyCredential(key);
        // DocumentIntelligenceClient client = new DocumentIntelligenceClient(new Uri(endpoint), credential);
        DocumentAnalysisClient client = new DocumentAnalysisClient(new Uri(endpoint), credential);

        //sample document
        System.IO.Stream fileStream = File.OpenRead(fileInfo.FullName);

        AnalyzeDocumentOperation operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-layout", fileStream);
        AnalyzeResult result = operation.Value;

        foreach (DocumentPage page in result.Pages)
        {
            Console.WriteLine($"Document Page {page.PageNumber} has {page.Lines.Count} line(s), {page.Words.Count} word(s),");

            // foreach (DocumentParagraph paragraph in result.Paragraphs)
            // {
            //     Console.WriteLine($"  Paragraph content: {paragraph.Content}");
            // }

            for (int i = 0; i < page.Lines.Count; i++)
            {
                DocumentLine line = page.Lines[i];
                // Console.WriteLine($"  Line {i} has content: '{line.Content}'.");
                await kernel.Memory.SaveInformationAsync(
                        collection: collection,
                        text: line.Content,
                        id: memoryId++.ToString(),
                        description: line.Content);
            }
        }
    }

    static async Task ImportFromCSVAsync(IKernel kernel, string collection, FileInfo fileInfo, int memoryId)
    {
        Console.WriteLine("Importing csv file.");

        var reader = new StreamReader(@fileInfo.FullName);

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            Console.WriteLine("CSV line: " + line);
            await kernel.Memory.SaveInformationAsync(
                        collection: collection,
                        text: line ?? "",
                        id: memoryId++.ToString(),
                        description: line);
        }
    }
}
