using System.Text.Json;
using KeelMatrix.EfGuard;

namespace KeelMatrix.EfGuard.Worker;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static async Task<int> Main(string[] args)
    {
        string? requestPath = args.Length == 2 && args[0] == "--request" ? args[1] : null;
        if (requestPath is null)
            return 2;

        ExtractionRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<ExtractionRequest>(await File.ReadAllTextAsync(requestPath).ConfigureAwait(false), JsonOptions);
            if (request is null)
                return 2;

            ExtractionResult result = await ExtractionWorker.ExtractAsync(request).ConfigureAwait(false);
            result.Notes = Reflection.RecordedNotes();
            await WriteResponseAsync(request.ResponsePath, result).ConfigureAwait(false);
            Reflection.WriteRecordedNotes();
            return result.Success ? 0 : 1;
        }
        catch
        {
            if (request?.ResponsePath is not null)
                await WriteResponseAsync(request.ResponsePath, new ExtractionResult { Success = false, Error = "The EF extraction worker failed." }).ConfigureAwait(false);
            return 1;
        }
    }

    private static Task WriteResponseAsync(string? path, ExtractionResult result)
        => path is null ? Task.CompletedTask : ExtractionResponse.WriteAsync(path, result, JsonOptions);
}
