using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using DesktopPet.Modules;
using NAudio.Wave;

namespace DesktopPet.SoundModule
{
    /// <summary>
    /// The Sound module (S2): plays the MP3 a pet animation triggers. Subscribes to the host's
    /// <see cref="IHost.AnimationStarted"/> event and, when it carries sound bytes, decodes + plays them
    /// via NAudio at the host volume. NAudio is this module's own dependency, isolated in the module's
    /// AssemblyLoadContext — the base app carries no audio codec. If no audio device is available (e.g. a
    /// headless CI runner) playback fails gracefully; the module never throws into the host.
    /// </summary>
    public sealed class SoundModule : IModule
    {
        private IHost _host;
        private readonly object _sync = new object();

        // One cached, replayable sound per distinct MP3 byte[]. The engine holds each pet's sound bytes for
        // the pet-type's lifetime and hands the SAME array instance on every AnimationStarted, so keying on
        // reference identity caches one decoded reader + output device per pet-sound (mirroring the base's
        // old pre-decoded TSound). All are disposed on Shutdown.
        private readonly Dictionary<byte[], Playing> _playing =
            new Dictionary<byte[], Playing>(ReferenceComparer.Instance);
        private bool _disposed;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "sound",
            Name = "Sound",
            Version = "1.0.0",
            MinHostVersion = "1.0.0",
            Permissions = ModulePermissions.None,   // benign local audio playback
        };

        public void Init(IHost host)
        {
            _host = host;
            host.AnimationStarted += OnAnimationStarted;
        }

        private void OnAnimationStarted(AnimationInfo info)
        {
            if (info == null || info.SoundData == null || info.SoundData.Length == 0) return;
            IHost host = _host;
            if (host == null) return;
            double volume = host.Volume;
            if (volume <= 0.0) return;   // muted: match the base's old "don't play at volume 0" behavior
            Play(info.SoundData, info.SoundLoop, volume);
        }

        private void Play(byte[] data, int loop, double volume)
        {
            lock (_sync)
            {
                if (_disposed) return;
                Playing p;
                if (!_playing.TryGetValue(data, out p))
                {
                    try { p = Playing.Create(data); }
                    catch { return; }   // undecodable (base already header-checked; be defensive)
                    _playing[data] = p;
                }
                p.Start(Math.Max(0, Math.Min(20, loop)), (float)volume);
            }
        }

        public void Shutdown()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                if (_host != null) { _host.AnimationStarted -= OnAnimationStarted; _host = null; }
                foreach (Playing p in _playing.Values) p.Dispose();
                _playing.Clear();
            }
        }

        /// <summary>
        /// Self-test hook (NOT part of the plugin ABI): returns true if NAudio — resolved in THIS module's
        /// own load context — can decode the given MP3. Lets the host prove the module ships + loads its
        /// codec in isolation without the base ever referencing NAudio. Invoked reflectively by
        /// --sound-selftest.
        /// </summary>
        public static bool DecodeProbe(byte[] mp3)
        {
            if (mp3 == null || mp3.Length == 0) return false;
            try
            {
                using (MemoryStream stream = new MemoryStream(mp3, false))
                using (Mp3FileReader reader = new Mp3FileReader(stream))
                    return reader.WaveFormat != null && reader.WaveFormat.SampleRate > 0;
            }
            catch { return false; }
        }

        /// <summary>One cached, replayable sound: an open MP3 reader + a lazily-created output device.</summary>
        private sealed class Playing : IDisposable
        {
            private readonly MemoryStream _stream;
            private readonly Mp3FileReader _reader;
            private WaveOutEvent _output;
            private int _loopsRemaining;
            private bool _disposed;

            private Playing(MemoryStream stream, Mp3FileReader reader) { _stream = stream; _reader = reader; }

            public static Playing Create(byte[] data)
            {
                MemoryStream stream = new MemoryStream(data, false);
                Mp3FileReader reader;
                try { reader = new Mp3FileReader(stream); }
                catch { stream.Dispose(); throw; }
                return new Playing(stream, reader);
            }

            public void Start(int loop, float volume)
            {
                if (_disposed) return;
                try
                {
                    if (_output == null)
                    {
                        WaveOutEvent output = new WaveOutEvent();
                        output.Init(_reader);
                        output.PlaybackStopped += OnStopped;
                        _output = output;
                    }
                    // A retrigger while this sound is still playing is a no-op (avoids restart races); a
                    // distinct sound has its own Playing/output and plays concurrently, as before.
                    if (_output.PlaybackState == PlaybackState.Playing) return;
                    _output.Volume = Math.Max(0f, Math.Min(1f, volume));
                    _loopsRemaining = loop;
                    _reader.Seek(0, SeekOrigin.Begin);
                    _output.Play();
                }
                catch
                {
                    // No audio device / device error: drop the output so a later attempt can retry, stay
                    // silent, and never throw into the host.
                    DisposeOutput();
                }
            }

            private void OnStopped(object sender, StoppedEventArgs e)
            {
                if (_disposed) return;
                if (e.Exception == null && _loopsRemaining-- > 0)
                {
                    try { _reader.Seek(0, SeekOrigin.Begin); _output.Play(); }
                    catch { DisposeOutput(); }
                }
            }

            private void DisposeOutput()
            {
                WaveOutEvent o = _output;
                _output = null;
                if (o != null) { o.PlaybackStopped -= OnStopped; try { o.Stop(); } catch { } o.Dispose(); }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                DisposeOutput();
                _reader.Dispose();
                _stream.Dispose();
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
