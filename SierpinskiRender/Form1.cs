using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;

namespace SierpinskiRender
{
    public partial class Form1 : Form
    {
        private volatile bool _running = false;
        private CancellationTokenSource _cts;
        private Task _renderTask;
        private Bitmap _bitmap;
        private byte[] _pixelData;
        private int _width;
        private int _height;
        private int _stride;
        private readonly object _sync = new object();

        private readonly Random _rand = new Random();
        private float _zoomFactor = 1.0f;

        // step/trace controls
        private long _stepDelayNs = 0;
        private bool _showTraces = false;

        // overlay trace data
        private bool _overlayVisible = false;
        private bool _traceHasData = false;
        private bool _overlayOneShotPending = false;
        private PointF _traceFrom, _traceTo, _traceMid;

        // Triangle vertices and current point (in pixel space)
        private PointF _v1, _v2, _v3;
        private PointF _current;

        public Form1()
        {
            InitializeComponent();
            // Ensure minimum size as requested
            this.MinimumSize = new Size(600, 600);
            // Initialize UI defaults
            if (trackZoom != null)
            {
                trackZoom.Value = 100; // 100%
                lblZoom.Text = $"Zoom: {trackZoom.Value}%";
            }
            if (numDelay != null)
            {
                _stepDelayNs = (long)numDelay.Value;
            }
            if (chkShowTraces != null)
            {
                _showTraces = chkShowTraces.Checked;
            }
            // Prepare initial canvas
            InitCanvas();
        }


        private void InitCanvas()
        {
            if (pictureBoxCanvas == null)
                return;

            var size = pictureBoxCanvas.ClientSize;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            StopRendering(); // Ensure not drawing while we rebuild

            lock (_sync)
            {
                _width = Math.Max(1, size.Width);
                _height = Math.Max(1, size.Height);
                _stride = _width * 4; // 32bpp, 4 bytes per pixel

                _pixelData = new byte[_stride * _height];
                // Fill white background
                for (int i = 0; i < _pixelData.Length; i += 4)
                {
                    _pixelData[i + 0] = 255; // B
                    _pixelData[i + 1] = 255; // G
                    _pixelData[i + 2] = 255; // R
                    _pixelData[i + 3] = 255; // A
                }

                _bitmap?.Dispose();
                _bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
                UpdateBitmapFromBuffer();
                // Wir zeichnen künftig im Paint-Handler selbst, um Lock-Konflikte zu vermeiden
                pictureBoxCanvas.Image = null;

                // reset any overlay trace
                _overlayVisible = false;
                _traceHasData = false;

                ComputeTriangleVertices();
                // Initialize current point at centroid
                _current = new PointF((_v1.X + _v2.X + _v3.X) / 3f, (_v1.Y + _v2.Y + _v3.Y) / 3f);
                // Run a few warm-up iterations without drawing to move into attractor
                for (int i = 0; i < 100; i++)
                {
                    NextChaosStep(draw: false);
                }
            }
        }

        private void ComputeTriangleVertices()
        {
            float w = _width;
            float h = _height;
            float min = Math.Min(w, h);
            // Apply zoom so that higher zoom values make the triangle larger (not smaller)
            float side = (min - 20f) * Math.Max(0.1f, _zoomFactor);
            float hTri = (float)(Math.Sqrt(3) / 2.0 * side);
            float cx = w / 2f;
            float cy = h / 2f;

            _v1 = new PointF(cx, cy - hTri / 2f);           // top
            _v2 = new PointF(cx - side / 2f, cy + hTri / 2f); // bottom-left
            _v3 = new PointF(cx + side / 2f, cy + hTri / 2f); // bottom-right
        }

        private void StartRendering()
        {
            if (_running)
                return;

            _running = true;
            btnStartStop.Text = "Stop";
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _renderTask = Task.Run(() => RenderLoop(token), token);
        }

        private void StopRendering()
        {
            if (!_running)
                return;

            try
            {
                _cts?.Cancel();
                _renderTask?.Wait(200);
            }
            catch { /* ignore */ }
            finally
            {
                _running = false;
                btnStartStop.Text = "Start";
                _cts?.Dispose();
                _cts = null;
                _renderTask = null;
            }
        }

        private void RenderLoop(CancellationToken token)
        {
            const int batchSize = 50000; // number of points per update
            try
            {
                while (!token.IsCancellationRequested)
                {
                    bool stepMode = _stepDelayNs > 0 || _showTraces;
                    if (stepMode)
                    {
                        StepInfo info;
                        lock (_sync)
                        {
                            info = NextChaosStepWithInfo(draw: true);
                            UpdateBitmapFromBuffer();
                            if (_showTraces)
                            {
                                _traceFrom = info.From;
                                _traceTo = info.Target;
                                _traceMid = info.Mid;
                                _traceHasData = true;
                                _overlayVisible = true;
                                _overlayOneShotPending = true;
                            }
                        }
                        if (pictureBoxCanvas.IsHandleCreated)
                        {
                            try { pictureBoxCanvas.BeginInvoke((Action)(() => pictureBoxCanvas.Invalidate())); } catch { }
                        }
                        long ns = _stepDelayNs;
                        if (ns > 0)
                        {
                            DelayNanoseconds(ns);
                        }
                        // Do not clear overlay here; let the Paint handler show it for one frame and hide it afterward.
                    }
                    else
                    {
                        lock (_sync)
                        {
                            for (int i = 0; i < batchSize; i++)
                            {
                                NextChaosStep(draw: true);
                            }
                            UpdateBitmapFromBuffer();
                        }
                        if (pictureBoxCanvas.IsHandleCreated)
                        {
                            try { pictureBoxCanvas.BeginInvoke((Action)(() => pictureBoxCanvas.Invalidate())); } catch { }
                        }
                        // Slight pause to keep UI responsive
                        Thread.Sleep(10);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { /* ignore */ }
        }

        private static void DelayNanoseconds(long nanoseconds)
        {
            if (nanoseconds <= 0) return;
            // Sleep for whole milliseconds if large enough
            if (nanoseconds >= 1_000_000)
            {
                int ms = (int)Math.Min(int.MaxValue, nanoseconds / 1_000_000);
                if (ms > 0)
                {
                    Thread.Sleep(ms);
                    nanoseconds -= ms * 1_000_000L;
                }
            }
            if (nanoseconds <= 0) return;
            long ticksRequired = (long)(nanoseconds * (double)Stopwatch.Frequency / 1_000_000_000.0);
            if (ticksRequired <= 0) return;
            long start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetTimestamp() - start < ticksRequired)
            {
                Thread.SpinWait(50);
            }
        }

        private struct StepInfo
        {
            public PointF From;
            public PointF Target;
            public PointF Mid;
        }

        private StepInfo NextChaosStepWithInfo(bool draw)
        {
            PointF from = _current;
            // Choose one of the three vertices randomly
            int r = _rand.Next(3);
            PointF target = r == 0 ? _v1 : (r == 1 ? _v2 : _v3);
            PointF mid = new PointF((from.X + target.X) / 2f, (from.Y + target.Y) / 2f);
            _current = mid;

            if (draw)
            {
                int x = (int)mid.X;
                int y = (int)mid.Y;
                if ((uint)x < (uint)_width && (uint)y < (uint)_height)
                {
                    int idx = y * _stride + x * 4;
                    // Black pixel, full alpha (BGRA)
                    _pixelData[idx + 0] = 0;   // B
                    _pixelData[idx + 1] = 0;   // G
                    _pixelData[idx + 2] = 0;   // R
                    _pixelData[idx + 3] = 255; // A
                }
            }
            return new StepInfo { From = from, Target = target, Mid = mid };
        }

        private void NextChaosStep(bool draw)
        {
            // Choose one of the three vertices randomly
            int r = _rand.Next(3);
            PointF target = r == 0 ? _v1 : (r == 1 ? _v2 : _v3);
            _current = new PointF((_current.X + target.X) / 2f, (_current.Y + target.Y) / 2f);

            if (draw)
            {
                int x = (int)_current.X;
                int y = (int)_current.Y;
                if ((uint)x < (uint)_width && (uint)y < (uint)_height)
                {
                    int idx = y * _stride + x * 4;
                    // Black pixel, full alpha (BGRA)
                    _pixelData[idx + 0] = 0;   // B
                    _pixelData[idx + 1] = 0;   // G
                    _pixelData[idx + 2] = 0;   // R
                    _pixelData[idx + 3] = 255; // A
                }
            }
        }

        private void UpdateBitmapFromBuffer()
        {
            if (_bitmap == null || _pixelData == null) return;
            var rect = new Rectangle(0, 0, _width, _height);
            BitmapData data = _bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                // In 32bppArgb, stride should be width*4. If not, copy row by row
                if (data.Stride == _stride)
                {
                    Marshal.Copy(_pixelData, 0, data.Scan0, _pixelData.Length);
                }
                else
                {
                    int copyWidth = Math.Min(_stride, Math.Abs(data.Stride));
                    int rows = _height;
                    IntPtr dest = data.Scan0;
                    int srcOffset = 0;
                    for (int y = 0; y < rows; y++)
                    {
                        Marshal.Copy(_pixelData, srcOffset, dest, copyWidth);
                        dest = IntPtr.Add(dest, data.Stride);
                        srcOffset += _stride;
                    }
                }
            }
            finally
            {
                _bitmap.UnlockBits(data);
            }
        }

        private RectangleF GetImageDisplayRectangle()
        {
            var pbRect = pictureBoxCanvas?.ClientRectangle ?? Rectangle.Empty;
            if (_width <= 0 || _height <= 0 || pbRect.Width <= 0 || pbRect.Height <= 0)
                return RectangleF.Empty;
            float imgW = _width, imgH = _height;
            float pbW = pbRect.Width, pbH = pbRect.Height;
            float scale = Math.Min(pbW / imgW, pbH / imgH);
            float dstW = imgW * scale;
            float dstH = imgH * scale;
            float x = pbRect.Left + (pbW - dstW) / 2f;
            float y = pbRect.Top + (pbH - dstH) / 2f;
            return new RectangleF(x, y, dstW, dstH);
        }

        private void pictureBoxCanvas_Paint(object sender, PaintEventArgs e)
        {
            // Owner-draw with defensive guards: if GDI+ complains about locked bitmap, skip this frame.
            lock (_sync)
            {
                try
                {
                    if (_bitmap == null || _width <= 0 || _height <= 0)
                        return;

                    var rect = GetImageDisplayRectangle();
                    if (rect.Width <= 0 || rect.Height <= 0) return;

                    var g = e.Graphics;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;

                    // Draw image
                    g.DrawImage(_bitmap, rect);

                    // Optional overlay
                    if (_overlayVisible && _showTraces && _traceHasData)
                    {
                        float sx = rect.Width / _width;
                        float sy = rect.Height / _height;
                        Func<PointF, PointF> map = p => new PointF(rect.Left + p.X * sx, rect.Top + p.Y * sy);
                        var pFrom = map(_traceFrom);
                        var pTo = map(_traceTo);
                        var pMid = map(_traceMid);

                        using (var pen = new Pen(Color.Lime, 2f))
                        {
                            g.DrawLine(pen, pFrom, pTo);
                        }
                        using (var brushT = new SolidBrush(Color.Red))
                        {
                            float r = 4f;
                            g.FillEllipse(brushT, pTo.X - r, pTo.Y - r, r * 2, r * 2);
                        }
                        using (var brushM = new SolidBrush(Color.Black))
                        {
                            float r2 = 3f;
                            g.FillEllipse(brushM, pMid.X - r2, pMid.Y - r2, r2 * 2, r2 * 2);
                        }

                        // One-shot: after drawing, hide the overlay so it displays for exactly one paint
                        if (_overlayOneShotPending)
                        {
                            _overlayOneShotPending = false;
                            _overlayVisible = false;
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // Ignore transient GDI+ state (e.g., bitmap momentarily locked). Next repaint will succeed.
                }
                catch
                {
                    // Swallow any unexpected paint-time GDI+ errors to keep UI stable.
                }
            }
        }

        private void chkShowTraces_CheckedChanged(object sender, EventArgs e)
        {
            _showTraces = chkShowTraces.Checked;
        }

        private void numDelay_ValueChanged(object sender, EventArgs e)
        {
            _stepDelayNs = (long)numDelay.Value;
        }

        private void btnStartStop_Click(object sender, EventArgs e)
        {
            if (_running) StopRendering(); else StartRendering();
        }

        private void btnExport_Click(object sender, EventArgs e)
        {
            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "Export Image";
                sfd.Filter = "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg|Bitmap Image (*.bmp)|*.bmp";
                sfd.AddExtension = true;
                sfd.FileName = "sierpinski";
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    lock (_sync)
                    {
                        if (_bitmap == null) return;
                        // Clone to avoid locking issues
                        using (var clone = new Bitmap(_bitmap))
                        {
                            var ext = System.IO.Path.GetExtension(sfd.FileName)?.ToLowerInvariant();
                            ImageFormat fmt = ImageFormat.Png;
                            if (ext == ".jpg" || ext == ".jpeg") fmt = ImageFormat.Jpeg;
                            else if (ext == ".bmp") fmt = ImageFormat.Bmp;
                            clone.Save(sfd.FileName, fmt);
                        }
                    }
                }
            }
        }

        private void trackZoom_ValueChanged(object sender, EventArgs e)
        {
            _zoomFactor = Math.Max(0.1f, trackZoom.Value / 100f);
            lblZoom.Text = $"Zoom: {trackZoom.Value}%";
            // Recompute vertices and clear image
            bool wasRunning = _running;
            InitCanvas();
            if (wasRunning) StartRendering();
        }

        private void pictureBoxCanvas_SizeChanged(object sender, EventArgs e)
        {
            // Re-create the image on resize as requested
            bool wasRunning = _running;
            InitCanvas();
            if (wasRunning) StartRendering();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopRendering();
            lock (_sync)
            {
                _bitmap?.Dispose();
                _bitmap = null;
            }
        }
    }
}