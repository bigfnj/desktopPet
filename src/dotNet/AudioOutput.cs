using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DesktopPet
{
    /// <summary>
    /// Host-owned audio output (B1): one shared mixer + output device that plays the pet's animation sounds
    /// today and the AI speech (TTS) engine later, through a single path. Pet MP3s are decoded once (ACM,
    /// the OS codec, so no native binary ships) into a cached float buffer at the mixer format; each play
    /// adds a volume-wrapped, optionally-looping input, so distinct sounds overlap, per-sound volume works,
    /// and speech can duck SFX once TTS arrives. Device errors are swallowed — a box with no audio device
    /// stays silent and never throws into the engine. NAudio (Core + WinMM) is a base dependency again as of
    /// B1: it left in S2 on the false premise that no pet shipped audio; every bundled pet does.
    ///
    /// Threading: this is the canonical NAudio "fire-and-forget" pattern — the WaveOut callback thread reads
    /// the mixer while callers add inputs; <see cref="MixingSampleProvider"/> guards its own source list, and
    /// the decode cache is guarded here.
    /// </summary>
    internal sealed class AudioOutput : IDisposable
    {
        private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        private readonly object _sync = new object();
        private readonly Dictionary<byte[], float[]> _cache =
            new Dictionary<byte[], float[]>(ReferenceComparer.Instance);
        private MixingSampleProvider _mixer;
        private WaveOut _output;
        private bool _started;
        private bool _unavailable;
        private bool _disposed;

        /// <summary>Play an MP3 (raw bytes) at <paramref name="volume"/> (0..1), repeating it
        /// <paramref name="loop"/> extra times (0 = play once). Silent + safe when volume is 0 or no device.</summary>
        public void Play(byte[] mp3, int loop, double volume)
        {
            if (mp3 == null || mp3.Length == 0 || volume <= 0.0) return;   // 0 volume = muted (pre-S2 behavior)
            lock (_sync)
            {
                if (_disposed || !EnsureStarted()) return;
                float[] samples;
                if (!_cache.TryGetValue(mp3, out samples))
                {
                    try { samples = Decode(mp3); }
                    catch { samples = null; }
                    _cache[mp3] = samples;   // cache even null so an undecodable sound isn't retried each trigger
                }
                if (samples == null || samples.Length == 0) return;
                var cached = new CachedSampleProvider(samples, MixFormat, Math.Max(0, Math.Min(20, loop)));
                var scaled = new VolumeSampleProvider(cached) { Volume = (float)Math.Max(0.0, Math.Min(1.0, volume)) };
                try { _mixer.AddMixerInput(scaled); } catch { }
            }
        }

        private bool EnsureStarted()
        {
            if (_started) return true;
            if (_unavailable) return false;
            try
            {
                _mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };   // keep running when idle
                _output = new WaveOut();
                _output.Init(_mixer.ToWaveProvider());
                _output.Play();
                _started = true;
                return true;
            }
            catch
            {
                _unavailable = true;   // no audio device: stay silent, don't retry on every trigger
                DisposeOutput();
                return false;
            }
        }

        private static float[] Decode(byte[] mp3)
        {
            using (var ms = new MemoryStream(mp3, false))
            using (var reader = new Mp3FileReaderBase(ms, wf => new AcmMp3FrameDecompressor(wf)))
            {
                ISampleProvider sp = reader.ToSampleProvider();
                if (sp.WaveFormat.SampleRate != MixFormat.SampleRate)
                    sp = new WdlResamplingSampleProvider(sp, MixFormat.SampleRate);
                if (sp.WaveFormat.Channels == 1)
                    sp = new MonoToStereoSampleProvider(sp);
                // sp is now IeeeFloat at the mixer's rate + channels; read it all into one buffer.
                var all = new List<float>(1 << 16);
                float[] buf = new float[8192];
                int n;
                while ((n = sp.Read(buf.AsSpan())) > 0)
                    for (int i = 0; i < n; i++) all.Add(buf[i]);
                return all.ToArray();
            }
        }

        private void DisposeOutput()
        {
            WaveOut o = _output;
            _output = null; _mixer = null; _started = false;
            if (o != null) { try { o.Stop(); } catch { } o.Dispose(); }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                DisposeOutput();
                _cache.Clear();
            }
        }

        /// <summary>Reads a cached float buffer (already at the mixer format), repeating a fixed number of
        /// extra times, then returns 0 so the mixer drops it.</summary>
        private sealed class CachedSampleProvider : ISampleProvider
        {
            private readonly float[] _samples;
            private readonly WaveFormat _format;
            private int _position;
            private int _loopsRemaining;
            public CachedSampleProvider(float[] samples, WaveFormat format, int loops)
            {
                _samples = samples; _format = format; _loopsRemaining = loops;
            }
            public WaveFormat WaveFormat { get { return _format; } }
            // NAudio 3 modernized ISampleProvider to a Span-based Read.
            public int Read(Span<float> buffer)
            {
                int count = buffer.Length;
                int written = 0;
                while (written < count)
                {
                    int available = _samples.Length - _position;
                    if (available <= 0)
                    {
                        if (_loopsRemaining <= 0) break;
                        _loopsRemaining--; _position = 0; available = _samples.Length;
                        if (available <= 0) break;
                    }
                    int take = Math.Min(available, count - written);
                    _samples.AsSpan(_position, take).CopyTo(buffer.Slice(written, take));
                    _position += take; written += take;
                }
                return written;
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<byte[]>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(byte[] x, byte[] y) { return ReferenceEquals(x, y); }
            public int GetHashCode(byte[] obj) { return RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
