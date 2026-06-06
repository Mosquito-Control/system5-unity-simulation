using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DroneSim
{
    /// <summary>
    /// One ffmpeg child process per camera: raw RGBA frames in on stdin, H.264 RTSP out.
    /// Owns a writer thread with a small drop-oldest queue so a stalled encoder can never
    /// block the render loop. "auto" encoder mode tries NVENC and falls back to libx264
    /// if the process dies early.
    /// </summary>
    public class FfmpegPipe : IDisposable
    {
        readonly string _camName, _ffmpegPath, _rtspUrl;
        readonly int _width, _height, _fps, _bitrateKbps, _frameBytes;
        readonly bool _autoFallback;
        string _encoder; // "nvenc" | "x264"

        Process _proc;
        readonly Thread _writerThread;
        readonly BlockingCollection<byte[]> _queue = new BlockingCollection<byte[]>(3);
        readonly ConcurrentBag<byte[]> _pool = new ConcurrentBag<byte[]>();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        DateTime _procStartUtc;
        int _restarts;
        long _dropped;
        volatile string _lastStderr = "";
        volatile bool _disposed;

        const int MaxRestarts = 5;

        public FfmpegPipe(string camName, int width, int height, int fps, int bitrateKbps,
                          string encoderMode, string ffmpegPath, string rtspUrl)
        {
            _camName = camName; _width = width; _height = height; _fps = fps;
            _bitrateKbps = bitrateKbps; _rtspUrl = rtspUrl;
            _ffmpegPath = ResolveFfmpegPath(ffmpegPath);
            _frameBytes = width * height * 4;
            _autoFallback = encoderMode == "auto";
            // auto = platform hardware encoder first (nvenc / videotoolbox), x264 on failure
            _encoder = encoderMode == "auto" ? PlatformHardwareEncoder() : encoderMode;

            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = $"ffmpeg-{camName}" };
            _writerThread.Start();
        }

        public byte[] RentBuffer()
        {
            return _pool.TryTake(out var b) && b.Length == _frameBytes ? b : new byte[_frameBytes];
        }

        /// <summary>Main-thread entry. Never blocks; drops the oldest queued frame on backpressure.</summary>
        public void PushFrame(byte[] frame)
        {
            if (_disposed) { _pool.Add(frame); return; }
            if (!_queue.TryAdd(frame))
            {
                if (_queue.TryTake(out var old)) _pool.Add(old);
                if (!_queue.TryAdd(frame)) _pool.Add(frame);
                if ((++_dropped % 100) == 1)
                    Debug.LogWarning($"[Sim] {_camName}: encoder backpressure ({_dropped} frames dropped so far)");
            }
        }

        static string PlatformHardwareEncoder()
        {
            var p = Application.platform;
            return (p == RuntimePlatform.OSXPlayer || p == RuntimePlatform.OSXEditor)
                ? "videotoolbox" : "nvenc";
        }

        static string ResolveFfmpegPath(string configured)
        {
            if (configured != "ffmpeg") return configured; // explicit path in config wins
            var p = Application.platform;
            if (p == RuntimePlatform.OSXPlayer || p == RuntimePlatform.OSXEditor)
            {
                // A Finder-launched .app does NOT inherit the shell PATH — probe brew locations.
                if (System.IO.File.Exists("/opt/homebrew/bin/ffmpeg")) return "/opt/homebrew/bin/ffmpeg"; // Apple Silicon
                if (System.IO.File.Exists("/usr/local/bin/ffmpeg")) return "/usr/local/bin/ffmpeg";       // Intel
            }
            return configured;
        }

        string BuildArgs()
        {
            string codec;
            if (_encoder == "nvenc")
                codec = "-c:v h264_nvenc -preset p1 -tune ull -zerolatency 1 -bf 0";
            else if (_encoder == "videotoolbox")
                codec = "-c:v h264_videotoolbox -realtime 1 -bf 0";
            else
                codec = "-c:v libx264 -preset ultrafast -tune zerolatency -bf 0";
            // Unity readback rows are bottom-up -> vflip restores normal orientation.
            // setpts=RTCTIME stamps every frame with wallclock at arrival (VFR), so the
            // stream stays real-time-correct even when the sim renders below target fps.
            // (use_wallclock_as_timestamps does NOT work here: the rawvideo demuxer
            // always generates index-based pts.)
            return "-hide_banner -loglevel warning " +
                   $"-f rawvideo -pix_fmt rgba -s {_width}x{_height} -r {_fps} -i pipe:0 " +
                   "-vf vflip,settb=AVTB,setpts=(RTCTIME-RTCSTART)/(TB*1000000) " +
                   // 1s keyframe interval: readers joining mid-stream recover fast
                   // (less decode_slice_header noise), worth the small bitrate cost
                   $"-an {codec} -g {_fps} -b:v {_bitrateKbps}k " +
                   "-fps_mode passthrough " +
                   $"-f rtsp -rtsp_transport tcp {_rtspUrl}";
        }

        bool StartProcess()
        {
            try
            {
                _proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = BuildArgs(),
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardError = true, // must be drained or ffmpeg blocks
                        CreateNoWindow = true
                    }
                };
                _proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) _lastStderr = e.Data; };
                _proc.Start();
                _proc.BeginErrorReadLine();
                _procStartUtc = DateTime.UtcNow;
                Debug.Log($"[Sim] {_camName}: ffmpeg up ({_encoder}) -> {_rtspUrl}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Sim] {_camName}: cannot start '{_ffmpegPath}' ({e.Message}). Streaming disabled for this camera.");
                _proc = null;
                _restarts = MaxRestarts + 1; // binary missing — don't spin
                return false;
            }
        }

        void WriterLoop()
        {
            StartProcess();
            while (!_cts.IsCancellationRequested)
            {
                byte[] frame;
                try { frame = _queue.Take(_cts.Token); }
                catch (OperationCanceledException) { break; }

                if (_proc == null || _proc.HasExited)
                {
                    _pool.Add(frame);
                    if (!TryRestart()) break;
                    continue;
                }

                try
                {
                    var stdin = _proc.StandardInput.BaseStream;
                    stdin.Write(frame, 0, _frameBytes);
                    stdin.Flush();
                }
                catch (Exception)
                {
                    // broken pipe — encoder died; restart logic runs on the next frame
                }
                _pool.Add(frame);
            }
        }

        bool TryRestart()
        {
            if (_disposed || _cts.IsCancellationRequested) return false;

            double upSecs = (DateTime.UtcNow - _procStartUtc).TotalSeconds;
            if (_autoFallback && _encoder != "x264" && upSecs < 10)
            {
                Debug.LogWarning($"[Sim] {_camName}: {_encoder} failed early ('{_lastStderr}') — falling back to libx264");
                _encoder = "x264";
                _restarts = 0;
                return StartProcess();
            }
            if (++_restarts > MaxRestarts)
            {
                // Never give up permanently: a publisher conflict or MediaMTX restart is
                // transient and the stream must come back on its own (server runs unattended).
                if (_restarts == MaxRestarts + 1)
                    Debug.LogError($"[Sim] {_camName}: ffmpeg failed {MaxRestarts} times — demoting to slow retry (every 10s). Last stderr: {_lastStderr}");
                Thread.Sleep(10000);
                return StartProcess();
            }
            Thread.Sleep(2000); // backoff on the writer thread, main loop unaffected
            Debug.LogWarning($"[Sim] {_camName}: restarting ffmpeg (attempt {_restarts}/{MaxRestarts}). Last stderr: {_lastStderr}");
            return StartProcess();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try { _writerThread?.Join(1000); } catch { /* best effort */ }
            try
            {
                if (_proc != null && !_proc.HasExited)
                {
                    try { _proc.StandardInput.Close(); } catch { }
                    if (!_proc.WaitForExit(2000)) _proc.Kill();
                }
            }
            catch { /* already gone */ }
            _proc = null;
        }
    }
}
