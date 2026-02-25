using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;

namespace LiveTranscriptionApp
{
    public static class ModelDownloader
    {
        public static async Task<string> EnsureModelExists(string modelName = "base.en")
        {
            var modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{modelName}.bin");
            
            if (File.Exists(modelPath))
            {
                return modelPath;
            }

            Console.WriteLine($"Downloading model {modelName}...");
            
            var modelType = GgmlType.BaseEn;
            if (modelName == "tiny") modelType = GgmlType.Tiny;
            else if (modelName == "tiny.en") modelType = GgmlType.TinyEn;
            else if (modelName == "small.en") modelType = GgmlType.SmallEn;
            else if (modelName == "base.en") modelType = GgmlType.BaseEn;
            else if (modelName == "turbo") modelType = GgmlType.LargeV3Turbo;

            try
            {
                using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(modelType);
                using (var fileStream = File.Create(modelPath))
                {
                    await modelStream.CopyToAsync(fileStream);
                }

                // Verify the file was created and has content
                var fileInfo = new FileInfo(modelPath);
                if (fileInfo.Length < 1000000) // Whisper models are generally > 1MB
                {
                    throw new Exception($"Downloaded model file is too small ({fileInfo.Length} bytes). It might be corrupted.");
                }

                return modelPath;
            }
            catch (Exception ex)
            {
                // Prevent corrupted partial downloads from breaking future launches
                if (File.Exists(modelPath))
                {
                    try { File.Delete(modelPath); } catch { /* Ignore cleanup errors */ }
                }
                throw new Exception($"Failed to download or verify Whisper model: {ex.Message}", ex);
            }
        }
    }
}
