using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Net.Http;
using Gst;
using Whisper.net;
using Whisper.net.Ggml;

namespace LiveTranscriptionApp
{
    public class TranscriptionService
    {
        private Pipeline? _pipeline;
        private WhisperProcessor? _processor;
        private WhisperFactory? _factory;
        private readonly Action<string, bool> _onTranscription;
        private readonly Action<float>? _onAudioLevel;
        private bool _isRunning;
        private System.DateTime _lastLevelUpdate = System.DateTime.MinValue;
        private System.DateTime _lastVoiceActivity = System.DateTime.UtcNow;

        public TranscriptionService(Action<string, bool> onTranscription, Action<float>? onAudioLevel = null)
        {
            _onTranscription = onTranscription;
            _onAudioLevel = onAudioLevel;

            // Timer fires 1.2s after last voice activity — commits partial to line1
            _silenceTimer = new System.Threading.Timer(_ =>
            {
                if (!_committed && !string.IsNullOrEmpty(_lastPartialText))
                {
                    _onTranscription?.Invoke(_lastPartialText, true);
                    _lastPartialText = "";
                    _committed = true;
                    lock (_bufferLock) { _sessionBuffer.Clear(); }
                }
            }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        public System.Threading.Tasks.Task InitializeAsync(string modelPath)
        {
            // Initialize Whisper
            _factory = WhisperFactory.FromPath(modelPath);
            _processor = _factory.CreateBuilder()
                .WithLanguage("en")
                .Build();

            // Initialize GStreamer
            Gst.Application.Init();

            // Create Pipeline: wasapi2src ! audioconvert ! audioresample ! appsink
            _pipeline = new Pipeline("transcription-pipeline");
            var source = ElementFactory.Make("wasapi2src", "source");
            if (source != null) {
                source["loopback"] = true; // Capture system default speaker audio
            }
            var convert = ElementFactory.Make("audioconvert", "convert");
            var resample = ElementFactory.Make("audioresample", "resample");
            var sink = ElementFactory.Make("appsink", "sink");

            if (source == null || convert == null || resample == null || sink == null)
            {
                var missing = string.Join(", ", 
                    new[] { 
                        source == null ? "wasapi2src" : null,
                        convert == null ? "audioconvert" : null,
                        resample == null ? "audioresample" : null,
                        sink == null ? "appsink" : null 
                    }.Where(x => x != null));
                throw new Exception($"Could not create GStreamer elements: {missing}. Ensure GStreamer is installed.");
            }

            // Configure Sink for 16kHz Mono 16-bit PCM using generic property/signal interface
            sink["emit-signals"] = true;
            sink["caps"] = Caps.FromString("audio/x-raw,format=S16LE,channels=1,rate=16000");
            sink.Connect("new-sample", OnNewSampleSignal);

            _pipeline.Add(source, convert, resample, sink);
            Element.Link(source, convert, resample, sink);

            return System.Threading.Tasks.Task.CompletedTask;
        }

        public void Start()
        {
            if (_pipeline != null)
            {
                var ret = _pipeline.SetState(State.Playing);
                _isRunning = true;
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _pipeline?.SetState(State.Null);
        }

        private readonly List<byte> _audioBuffer = new List<byte>();
        private readonly object _bufferLock = new object();
        private const int ChunkSize = 16000 * 2 / 4;        // 0.25 seconds per chunk
        private const int MinSessionBytes = 16000 * 2 / 2;      // 0.5 second minimum before first transcription
        private const int MaxSessionBytes = 16000 * 2 * 8;  // 8 second max session buffer for VAD tracking
        private const int LiveWindowBytes = 16000 * 2 * 2;  // Feed Whisper the last 2 seconds
        private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _audioQueue = new();
        private readonly System.Threading.SemaphoreSlim _audioSignal = new(0);
        private bool _loopStarted = false;
        private readonly List<byte> _sessionBuffer = new List<byte>();
        private volatile bool _isBusy = false;
        private string _lastPartialText = "";
        private bool _committed = true;
        private System.Threading.Timer? _silenceTimer; // Fires commit after 1.2s of silence

        private void OnNewSampleSignal(object sender, GLib.SignalArgs args)
        {
            if (!_isRunning) return;

            if (!_loopStarted)
            {
                _loopStarted = true;
                _ = System.Threading.Tasks.Task.Run(TranscriptionLoop);
            }

            var sink = sender as Element;
            var sample = sink?.Emit("pull-sample") as Sample;
            if (sample == null) 
            {
                return;
            }

            using (var buffer = sample.Buffer)
            {
                if (buffer.Map(out var map, MapFlags.Read))
                {
                    if (_onAudioLevel != null || _isRunning)
                    {
                        var now = System.DateTime.UtcNow;
                        if ((now - _lastLevelUpdate).TotalMilliseconds > 40) // ~25 fps throttle
                        {
                            _lastLevelUpdate = now;
                            var data = map.Data;
                            int maxVal = 0;
                            for (int i = 0; i < data.Length - 1; i += 2)
                            {
                                short val = (short)(data[i] | (data[i + 1] << 8));
                                int absVal = Math.Abs((int)val);
                                if (absVal > maxVal) maxVal = absVal;
                            }
                            float level = maxVal / 32768f;
                            
                            if (level > 0.05f)
                            {
                                _lastVoiceActivity = now;
                                // Reset silence timer: will fire 1.2s after last voice activity
                                _silenceTimer?.Change(1200, System.Threading.Timeout.Infinite);
                            }
                            
                            _onAudioLevel?.Invoke(level);
                        }
                    }

                    lock (_bufferLock)
                    {
                        _audioBuffer.AddRange(map.Data);
                        if (_audioBuffer.Count >= ChunkSize)
                        {
                            var chunk = _audioBuffer.ToArray();
                            _audioBuffer.Clear();
                            _audioQueue.Enqueue(chunk);
                            _audioSignal.Release();
                        }
                    }
                    buffer.Unmap(map);
                }
            }
            sample.Dispose();
        }

        private async System.Threading.Tasks.Task TranscriptionLoop()
        {
            while (true)
            {
                await _audioSignal.WaitAsync();

                // If Whisper is still busy, drain stale queue entries (keep only latest)
                if (_isBusy)
                {
                    // Drain all pending: the next call will get them when Whisper finishes
                    while (_audioQueue.TryDequeue(out var staleChunk))
                    {
                        lock (_bufferLock) { _sessionBuffer.AddRange(staleChunk); }
                        // Cap session buffer to prevent unbounded growth
                        lock (_bufferLock)
                        {
                            if (_sessionBuffer.Count > MaxSessionBytes)
                                _sessionBuffer.RemoveRange(0, _sessionBuffer.Count - MaxSessionBytes);
                        }
                    }
                    continue;
                }

                if (_audioQueue.TryDequeue(out var chunk))
                {
                    try 
                    {
                        lock (_bufferLock)
                        {
                            _sessionBuffer.AddRange(chunk);
                            // Cap session buffer size to keep Whisper fast
                            if (_sessionBuffer.Count > MaxSessionBytes)
                                _sessionBuffer.RemoveRange(0, _sessionBuffer.Count - MaxSessionBytes);
                        }
                        
                        // Don't process if buffer is too small (need at least 1 second)
                        if (_sessionBuffer.Count < MinSessionBytes) continue;
                        
                        _isBusy = true;
                        byte[] sessionSnapshot;
                        lock (_bufferLock)
                        {
                            // For LIVE inference: only take the last 2 seconds — keeps Whisper fast
                            int start = Math.Max(0, _sessionBuffer.Count - LiveWindowBytes);
                            sessionSnapshot = _sessionBuffer.GetRange(start, _sessionBuffer.Count - start).ToArray();
                        }
                        
                        // Whisper.net expects a WAV stream (with RIFF header)
                        var wavStream = new MemoryStream(AddWavHeader(sessionSnapshot));
                        
                        string fullText = "";
                        await foreach (var result in _processor!.ProcessAsync(wavStream))
                        {
                            fullText += result.Text;
                        }

                        fullText = fullText.Trim();

                        // Emit partial update if text is valid (timer handles the isFinal commit)
                        if (!string.IsNullOrEmpty(fullText) && !fullText.StartsWith("[") && !fullText.StartsWith("("))
                        {
                            _lastPartialText = fullText;
                            _committed = false;
                            _onTranscription?.Invoke(fullText, false);
                        }

                        // Clear buffer after 3s of silence to prevent stale audio
                        if ((System.DateTime.UtcNow - _lastVoiceActivity).TotalSeconds > 3.0)
                        {
                            lock (_bufferLock) { _sessionBuffer.Clear(); }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Transcription error: {ex.Message}");
                    }
                    finally
                    {
                        _isBusy = false;
                    }
                }
            }
        }

        private byte[] AddWavHeader(byte[] samples)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            writer.Write("RIFF".ToCharArray());
            writer.Write(36 + samples.Length);
            writer.Write("WAVE".ToCharArray());
            writer.Write("fmt ".ToCharArray());
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write((short)1); // Mono
            writer.Write(16000);    // Rate
            writer.Write(16000 * 2); // Byte rate
            writer.Write((short)2); // Block align
            writer.Write((short)16); // Bits per sample
            writer.Write("data".ToCharArray());
            writer.Write(samples.Length);
            writer.Write(samples);

            return stream.ToArray();
        }
    }
}
