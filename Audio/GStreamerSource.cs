using System;
using Gst;

namespace LiveTranscriptionApp.Audio
{
    /// <summary>
    /// GStreamer-based audio source.
    /// Captures system audio (loopback) via wasapi2src and produces
    /// 16kHz Mono S16LE PCM chunks via the OnAudioData event.
    /// </summary>
    public class GStreamerSource : IAudioResource
    {
        public event AudioDataHandler? OnAudioData;

        public float Level { get; private set; }

        private Pipeline? _pipeline;
        private bool _isRunning;
        private System.DateTime _lastLevelUpdate = System.DateTime.MinValue;
        private const float SilenceThreshold = 0.05f;

        public void Initialize()
        {
            Gst.Application.Init();

            _pipeline = new Pipeline("gst-audio-source");
            var source  = ElementFactory.Make("wasapi2src",   "source");
            var convert = ElementFactory.Make("audioconvert", "convert");
            var resample= ElementFactory.Make("audioresample","resample");
            var sink    = ElementFactory.Make("appsink",      "sink");

            if (source == null || convert == null || resample == null || sink == null)
            {
                var missing = string.Join(", ",
                    new[] {
                        source   == null ? "wasapi2src"    : null,
                        convert  == null ? "audioconvert"  : null,
                        resample == null ? "audioresample" : null,
                        sink     == null ? "appsink"       : null
                    }.Where(x => x != null));
                throw new Exception($"GStreamer elements missing: {missing}. Is GStreamer installed?");
            }

            // Loopback = capture system speaker output (not microphone)
            source["loopback"] = true;

            // 16kHz, Mono, 16-bit signed little-endian PCM
            sink["emit-signals"] = true;
            sink["caps"] = Caps.FromString("audio/x-raw,format=S16LE,channels=1,rate=16000");
            sink.Connect("new-sample", OnNewSample);

            _pipeline.Add(source, convert, resample, sink);
            Element.Link(source, convert, resample, sink);
        }

        public void Start()
        {
            if (_pipeline == null) throw new InvalidOperationException("Call Initialize() first.");
            _isRunning = true;
            _pipeline.SetState(State.Playing);
        }

        public void Stop()
        {
            _isRunning = false;
            _pipeline?.SetState(State.Null);
        }

        private void OnNewSample(object sender, GLib.SignalArgs args)
        {
            if (!_isRunning) return;

            var sink   = sender as Element;
            var sample = sink?.Emit("pull-sample") as Sample;
            if (sample == null) return;

            using (var buffer = sample.Buffer)
            {
                if (buffer.Map(out var map, MapFlags.Read))
                {
                    // Measure and report audio level at ~25 fps
                    var now = System.DateTime.UtcNow;
                    if ((now - _lastLevelUpdate).TotalMilliseconds > 40)
                    {
                        _lastLevelUpdate = now;
                        var data   = map.Data;
                        int maxVal = 0;
                        for (int i = 0; i < data.Length - 1; i += 2)
                        {
                            short v = (short)(data[i] | (data[i + 1] << 8));
                            int   a = Math.Abs((int)v);
                            if (a > maxVal) maxVal = a;
                        }
                        Level = maxVal / 32768f;
                    }

                    // Fire audio data event with a copy of the PCM bytes
                    OnAudioData?.Invoke((byte[])map.Data.Clone());
                    buffer.Unmap(map);
                }
            }

            sample.Dispose();
        }
    }
}
